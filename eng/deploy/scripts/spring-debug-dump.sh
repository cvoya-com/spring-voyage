#!/usr/bin/env bash
# spring-debug-dump.sh — collect a complete diagnostic bundle from a local
# Podman deployment of Spring Voyage, for offline debugging of runtime
# behaviour (message loops, runaway agents, stuck units, cost blow-ups, …).
#
# It captures, best-effort and continue-on-error, everything that helps
# reconstruct "what the system did":
#
#   db/        full pg_dump (schema+data) + schema-only + per-table JSON +
#              the Dapr actor state table; PLUS enriched, human-readable
#              exports of the message interactions and the activity stream
#              (participant names resolved, content as markdown + plain text,
#              threads = unique participant sets, per-thread/-participant
#              aggregates, activity timeline with per-event cost).
#   logs/      `podman logs` for every spring-* container, including the
#              Dapr sidecars and the ephemeral/persistent agent runtime
#              containers (the ones that churn during a loop).
#   infra/     podman ps/inspect/stats/info, networks, volumes, images, and
#              a bounded `podman events` timeline — restart policy + restart
#              count per container live here (why containers respawn).
#   redis/     redis INFO + key inventory + values (Dapr / actor keys).
#   dapr/      Dapr sidecar metadata API snapshot (best-effort).
#   host/      dispatcher + resolved env (the launcher that starts agents).
#   MANIFEST.md + collection.log + skipped.txt — what ran, what didn't.
#
# Every section degrades gracefully: a stopped Postgres, a missing Redis, an
# already-torn-down agent container, no python3 — each is noted and skipped,
# the rest still collected. Run it BEFORE tearing a deployment down to capture
# the live state; it also works against a partially-stopped deployment.
#
# ⚠️  The bundle contains SECRETS (DB rows include API tokens / credentials;
#     `podman inspect` and the env files include passwords). It is written
#     with a `.gitignore` of `*` and MUST NOT be committed or shared as-is.
#
# Usage:
#   spring-debug-dump.sh [--output DIR] [--no-archive] [--no-db] [--no-logs]
#                        [--max-rows N] [--help]
#
# Env overrides: SPRING_DEBUG_OUT, SPRING_PG_CONTAINER (default spring-postgres),
#   SPRING_REDIS_CONTAINER (default spring-redis), SPRING_HOST_STATE_DIR
#   (default ~/.spring-voyage/host), PODMAN (default: podman).
#
# Exit 0 if the bundle was produced (even with skipped sections); non-zero only
# on a fatal setup error (no podman, cannot create output dir).

set -uo pipefail   # NOT -e: this is a best-effort collector; keep going on errors.

# ----------------------------------------------------------------- conventions
# Mirror deploy.sh: fixed platform containers, the runtime-container pattern,
# and the spring-* prefix used for networks/volumes.
PLATFORM_CONTAINERS=(
    spring-postgres spring-redis spring-placement spring-scheduler
    spring-worker-dapr spring-api-dapr spring-worker spring-api
    spring-web spring-caddy spring-ollama
)
SPRING_PREFIX_RE='^spring-'

PODMAN="${PODMAN:-podman}"
PG_CONTAINER="${SPRING_PG_CONTAINER:-spring-postgres}"
REDIS_CONTAINER="${SPRING_REDIS_CONTAINER:-spring-redis}"
HOST_STATE_DIR="${SPRING_HOST_STATE_DIR:-${HOME}/.spring-voyage/host}"

DO_ARCHIVE=1
DO_DB=1
DO_LOGS=1
MAX_ROWS=200000   # tables larger than this skip per-table JSON (pg_dump still has them)
OUT=""

# --------------------------------------------------------------------- args
usage() { sed -n '2,40p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --output) OUT="$2"; shift 2 ;;
        --output=*) OUT="${1#*=}"; shift ;;
        --no-archive) DO_ARCHIVE=0; shift ;;
        --no-db) DO_DB=0; shift ;;
        --no-logs) DO_LOGS=0; shift ;;
        --max-rows) MAX_ROWS="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage; exit 2 ;;
    esac
done

command -v "${PODMAN}" >/dev/null 2>&1 || { echo "FATAL: '${PODMAN}' not found on PATH" >&2; exit 1; }

# Timestamp without relying on a particular date(1) flavour for the dir name.
TS="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="${OUT:-${SPRING_DEBUG_OUT:-./spring-debug-dump-${TS}}}"
mkdir -p "${OUT}" || { echo "FATAL: cannot create output dir ${OUT}" >&2; exit 1; }
OUT="$(cd "${OUT}" && pwd)"   # absolutise

LOG="${OUT}/collection.log"
SKIPS="${OUT}/skipped.txt"
: >"${LOG}"; : >"${SKIPS}"
# The bundle holds secrets — never let it be committed accidentally.
printf '*\n' >"${OUT}/.gitignore"

# --------------------------------------------------------------------- helpers
log()  { printf '%s\n' "$*" | tee -a "${LOG}" >&2; }
note() { printf '\n>>> %s\n' "$*" >>"${LOG}"; }

# run "<description>" '<shell command, may contain pipes/redirs to $OUT/...>'
# stdout+stderr of the command go to the collection log; the command itself is
# responsible for redirecting payload to its destination file.
run() {
    local desc="$1"; shift
    local cmd="$*"
    printf '\n>>> %s\n    $ %s\n' "${desc}" "${cmd}" >>"${LOG}"
    local rc=0
    eval "${cmd}" >>"${LOG}" 2>&1 || rc=$?
    if [[ "${rc}" -eq 0 ]]; then
        printf '    ok\n' >>"${LOG}"
    else
        printf '    FAILED rc=%s\n' "${rc}" >>"${LOG}"
        printf -- '- %s (rc=%s)\n' "${desc}" "${rc}" >>"${SKIPS}"
    fi
    return "${rc}"
}

# table_json <schema> <table> <dest> — dump a table as a JSON array. Called
# directly (NOT via run/eval) so the SQL string keeps its real quotes; `$$`
# dollar-quoting cannot be used here because eval would expand it to the PID.
table_json() {
    local schema="$1" table="$2" dest="$3" rc=0
    psql_q -At -c "SELECT coalesce(json_agg(t), '[]'::json) FROM \"${schema}\".\"${table}\" t" \
        >"${dest}" 2>>"${LOG}" || rc=$?
    [[ "${rc}" -eq 0 ]] || printf -- '- table json %s.%s (rc=%s)\n' "${schema}" "${table}" "${rc}" >>"${SKIPS}"
    return "${rc}"
}

container_exists() { "${PODMAN}" container exists "$1" 2>/dev/null; }
container_running() { [[ "$("${PODMAN}" inspect -f '{{.State.Running}}' "$1" 2>/dev/null)" == "true" ]]; }

# psql_q — for `-c` queries. stdin is /dev/null so it is SAFE to call inside a
# `while read` loop: a bare `podman exec -i` would drain the loop's own stdin
# (the process substitution feeding it), truncating the loop after one pass.
psql_q()  { "${PODMAN}" exec "${PG_CONTAINER}" psql -U "${PGUSER}" -d "${PGDB}" "$@" </dev/null; }
# psql_in — for piping a .sql file in on stdin; needs `exec -i` to forward it.
psql_in() { "${PODMAN}" exec -i "${PG_CONTAINER}" psql -U "${PGUSER}" -d "${PGDB}" "$@"; }

# write_extract_sql — emit the single-statement, atomic-snapshot extraction
# query (messages + threads + participants + activity stream) into tools/.
write_extract_sql() {
cat > "${OUT}/tools/extract.sql" <<'SQL'
-- One JSON object: { meta, tenantId, participants, threads, messages, activities }.
-- A single statement => a consistent snapshot even while the system is live.
WITH dir AS (
    SELECT DISTINCT key AS ref, value AS name
    FROM spring.threads, jsonb_each_text(participant_name_snapshots)
),
iso AS (SELECT 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"' AS fmt)
SELECT json_build_object(
    'meta', (SELECT json_build_object(
        'messageCount', count(*),
        'minSentAt', to_char(min(sent_at) AT TIME ZONE 'UTC', (SELECT fmt FROM iso)),
        'maxSentAt', to_char(max(sent_at) AT TIME ZONE 'UTC', (SELECT fmt FROM iso)),
        'retractedCount', count(*) FILTER (WHERE retracted_at IS NOT NULL)
    ) FROM spring.messages),
    'tenantId', (SELECT tenant_id FROM spring.messages LIMIT 1),
    'participants', (SELECT json_agg(json_build_object(
        'ref', d.ref, 'scheme', split_part(d.ref, ':', 1), 'name', d.name
    ) ORDER BY d.ref) FROM dir d),
    'threads', (SELECT json_agg(json_build_object(
        'threadId', t.id, 'participantKey', t.participant_key,
        'participants', t.participants, 'participantNames', t.participant_name_snapshots,
        'createdAt', to_char(t.created_at AT TIME ZONE 'UTC', (SELECT fmt FROM iso)),
        'lastActivityAt', to_char(t.last_activity_at AT TIME ZONE 'UTC', (SELECT fmt FROM iso))
    ) ORDER BY t.created_at) FROM spring.threads t),
    'messages', (SELECT json_agg(json_build_object(
        'messageId', m.id, 'threadId', m.thread_id, 'type', m.message_type,
        'from', json_build_object('ref', m.sender_scheme||':'||replace(m.sender_id::text,'-',''),
                                  'scheme', m.sender_scheme, 'id', m.sender_id, 'name', sd.name),
        'to', json_build_object('ref', m.recipient_scheme||':'||replace(m.recipient_id::text,'-',''),
                                'scheme', m.recipient_scheme, 'id', m.recipient_id, 'name', rd.name),
        'sentAt', to_char(m.sent_at AT TIME ZONE 'UTC', (SELECT fmt FROM iso)),
        'sentAtEpochMs', round(extract(epoch FROM m.sent_at)*1000),
        'retractedAt', CASE WHEN m.retracted_at IS NULL THEN NULL
            ELSE to_char(m.retracted_at AT TIME ZONE 'UTC', (SELECT fmt FROM iso)) END,
        'body', m.body, 'payload', m.payload,
        'contentMarkdown', CASE
            WHEN jsonb_typeof(m.payload)='object' AND m.payload ? 'content' THEN m.payload->>'content'
            WHEN jsonb_typeof(m.payload)='string' THEN m.payload#>>'{}'
            ELSE m.body END
    ) ORDER BY m.sent_at, m.id)
    FROM spring.messages m
    LEFT JOIN dir sd ON sd.ref = m.sender_scheme||':'||replace(m.sender_id::text,'-','')
    LEFT JOIN dir rd ON rd.ref = m.recipient_scheme||':'||replace(m.recipient_id::text,'-','')),
    'activities', (SELECT json_agg(json_build_object(
        'id', a.id,
        'timestamp', to_char(a.timestamp AT TIME ZONE 'UTC', (SELECT fmt FROM iso)),
        'epochMs', round(extract(epoch FROM a.timestamp)*1000),
        'sourceId', a.source_id, 'eventType', a.event_type, 'severity', a.severity,
        'summary', a.summary, 'correlationId', a.correlation_id,
        'cost', a.cost, 'details', a.details
    ) ORDER BY a.timestamp, a.id) FROM spring.activity_events a)
);
SQL
}

# write_enrich_py — emit the stdlib-only post-processor that turns the raw
# snapshot into human-readable, self-contained per-record exports.
write_enrich_py() {
cat > "${OUT}/tools/enrich.py" <<'PY'
#!/usr/bin/env python3
"""Split raw_snapshot.json into enriched exports. Args: <raw_snapshot.json> <out_dir>."""
import json
import re
import sys
from collections import defaultdict
from datetime import datetime, timezone

RAW, OUT = sys.argv[1], sys.argv[2]


def md_to_plain(md):
    if md is None:
        return None
    t = md.replace("\r\n", "\n").replace("\r", "\n")
    t = re.sub(r"```[^\n]*\n(.*?)\n?```", lambda m: m.group(1), t, flags=re.DOTALL)
    t = re.sub(r"!\[([^\]]*)\]\([^)]*\)", r"\1", t)
    t = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", r"\1 (\2)", t)
    t = re.sub(r"(?m)^\s{0,3}#{1,6}\s+", "", t)
    t = re.sub(r"(?m)^\s{0,3}>\s?", "", t)
    t = re.sub(r"(?m)^\s{0,3}([-*_])(?:\s*\1){2,}\s*$", "", t)
    t = re.sub(r"(?m)^(\s*)[-*+]\s+", r"\1• ", t)
    t = re.sub(r"\*\*\*(.+?)\*\*\*", r"\1", t, flags=re.DOTALL)
    t = re.sub(r"\*\*(.+?)\*\*", r"\1", t, flags=re.DOTALL)
    t = re.sub(r"(?<!\w)\*(?!\s)(.+?)(?<!\s)\*(?!\w)", r"\1", t, flags=re.DOTALL)
    t = re.sub(r"___(.+?)___", r"\1", t, flags=re.DOTALL)
    t = re.sub(r"__(.+?)__", r"\1", t, flags=re.DOTALL)
    t = re.sub(r"(?<!\w)_(?!\s)(.+?)(?<!\s)_(?!\w)", r"\1", t, flags=re.DOTALL)
    t = re.sub(r"~~(.+?)~~", r"\1", t, flags=re.DOTALL)
    t = re.sub(r"`([^`]+)`", r"\1", t)
    return t


def hex_to_uuid(h):
    return f"{h[0:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:32]}"


def parse_ref(ref, name_map):
    scheme, _, hexid = ref.partition(":")
    return {"ref": ref, "scheme": scheme, "id": hex_to_uuid(hexid), "name": name_map.get(ref)}


def parse_iso(ts):
    return datetime.strptime(ts, "%Y-%m-%dT%H:%M:%S.%fZ").replace(tzinfo=timezone.utc)


def dump(name, obj):
    with open(f"{OUT}/{name}", "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)


def counter(it):
    c = defaultdict(int)
    for x in it:
        c[x] += 1
    return dict(sorted(c.items(), key=lambda kv: -kv[1]))


def main():
    with open(RAW, encoding="utf-8") as f:
        snap = json.load(f)
    tenant_id = snap.get("tenantId")
    parts_raw = snap.get("participants") or []
    name_map = {p["ref"]: p["name"] for p in parts_raw}
    hex_map = {}
    for p in parts_raw:
        scheme, _, hexid = p["ref"].partition(":")
        hex_map[hexid] = {"ref": p["ref"], "scheme": scheme, "name": p["name"]}

    raw_messages = snap.get("messages") or []
    raw_threads = snap.get("threads") or []
    raw_activities = snap.get("activities") or []

    # ---- messages
    messages = []
    for m in raw_messages:
        md = m.get("contentMarkdown")
        messages.append({
            "messageId": m["messageId"], "threadId": m["threadId"], "tenantId": tenant_id,
            "type": m["type"], "from": m["from"], "to": m["to"],
            "sentAt": m["sentAt"], "sentAtEpochMs": m["sentAtEpochMs"], "retractedAt": m["retractedAt"],
            "content": {"markdown": md, "plain": md_to_plain(md), "bodyRaw": m["body"]},
            "payload": m["payload"],
        })
    messages.sort(key=lambda x: (x["sentAt"], x["messageId"]))

    th_count = defaultdict(int); th_first = {}; th_last = {}
    p_sent = defaultdict(int); p_recv = defaultdict(int)
    p_threads = defaultdict(set); p_first = {}; p_last = {}
    direction = defaultdict(int)
    for m in messages:
        tid, ts = m["threadId"], m["sentAt"]
        th_count[tid] += 1
        th_first[tid] = min(th_first.get(tid, ts), ts)
        th_last[tid] = max(th_last.get(tid, ts), ts)
        fref, rref = m["from"]["ref"], m["to"]["ref"]
        p_sent[fref] += 1; p_recv[rref] += 1
        p_threads[fref].add(tid); p_threads[rref].add(tid)
        for ref in (fref, rref):
            p_first[ref] = min(p_first.get(ref, ts), ts)
            p_last[ref] = max(p_last.get(ref, ts), ts)
        direction[f'{m["from"]["scheme"]}->{m["to"]["scheme"]}'] += 1

    # ---- threads
    threads = []
    for t in raw_threads:
        tid = t["threadId"]
        names = t.get("participantNames") or {}
        parts = [parse_ref(r, names) for r in (t.get("participants") or [])]
        threads.append({
            "threadId": tid,
            "label": " ↔ ".join(sorted(p["name"] or p["ref"] for p in parts)),
            "participantKey": t["participantKey"], "participants": parts, "participantNames": names,
            "createdAt": t["createdAt"], "lastActivityAt": t["lastActivityAt"],
            "messageCount": th_count.get(tid, 0),
            "firstMessageAt": th_first.get(tid), "lastMessageAt": th_last.get(tid),
        })
    threads.sort(key=lambda x: x["createdAt"])

    # ---- participants
    participants = []
    for p in parts_raw:
        ref = p["ref"]; info = parse_ref(ref, name_map)
        info.update({
            "messagesSent": p_sent.get(ref, 0), "messagesReceived": p_recv.get(ref, 0),
            "threadsCount": len(p_threads.get(ref, ())),
            "firstActivityAt": p_first.get(ref), "lastActivityAt": p_last.get(ref),
        })
        participants.append(info)
    participants.sort(key=lambda x: (x["scheme"], x["name"] or ""))

    # ---- activities (the activity_events stream, source resolved)
    activities = []
    by_type = defaultdict(int); by_sev = defaultdict(int); by_source = defaultdict(int)
    total_cost = 0.0; problems = []
    for a in raw_activities:
        sid = a.get("sourceId")
        src = hex_map.get(sid.replace("-", "")) if sid else None
        rec = {
            "id": a["id"], "timestamp": a["timestamp"], "epochMs": a["epochMs"],
            "eventType": a["eventType"], "severity": a["severity"], "summary": a["summary"],
            "correlationId": a.get("correlationId"), "cost": a.get("cost"),
            "source": {"id": sid, "ref": src["ref"] if src else None,
                       "scheme": src["scheme"] if src else None, "name": src["name"] if src else None},
            "details": a.get("details"),
        }
        activities.append(rec)
        by_type[a["eventType"]] += 1
        by_sev[a["severity"]] += 1
        by_source[rec["source"]["name"] or rec["source"]["id"] or "?"] += 1
        if a.get("cost"):
            try:
                total_cost += float(a["cost"])
            except (TypeError, ValueError):
                pass
        if a["severity"] in ("Error", "Warning"):
            problems.append({"timestamp": a["timestamp"], "severity": a["severity"],
                             "eventType": a["eventType"], "summary": a["summary"],
                             "source": rec["source"]["name"]})
    activities.sort(key=lambda x: (x["timestamp"], x["id"]))

    # ---- write everything
    with open(f"{OUT}/messages.jsonl", "w", encoding="utf-8") as f:
        for m in messages:
            f.write(json.dumps(m, ensure_ascii=False) + "\n")
    with open(f"{OUT}/activities.jsonl", "w", encoding="utf-8") as f:
        for a in activities:
            f.write(json.dumps(a, ensure_ascii=False) + "\n")
    dump("messages.json", messages)
    dump("threads.json", threads)
    dump("participants.json", participants)

    def span(times):
        times = [t for t in times if t]
        if not times:
            return None
        s, e = min(times), max(times)
        return {"start": s, "end": e,
                "durationMinutes": round((parse_iso(e) - parse_iso(s)).total_seconds() / 60, 1)}

    dump("activities-summary.json", {
        "activityCount": len(activities),
        "timeSpan": span([a["timestamp"] for a in activities]),
        "byEventType": dict(sorted(by_type.items(), key=lambda kv: -kv[1])),
        "bySeverity": dict(by_sev),
        "bySource": dict(sorted(by_source.items(), key=lambda kv: -kv[1])),
        "totalCost": round(total_cost, 6),
        "errorAndWarningCount": len(problems),
        "errorsAndWarnings": problems[:500],
    })
    dump("summary.json", {
        "exportNote": ("Point-in-time snapshot from spring-debug-dump.sh. If collected while the "
                       "deployment was live, this is not a final/quiescent state."),
        "tenantId": tenant_id,
        "messageCount": len(messages), "threadCount": len(threads),
        "participantCount": len(participants), "activityCount": len(activities),
        "retractedCount": (snap.get("meta") or {}).get("retractedCount"),
        "totalActivityCost": round(total_cost, 6),
        "messageTimeSpan": span([m["sentAt"] for m in messages]),
        "byDirection": dict(sorted(direction.items(), key=lambda kv: -kv[1])),
        "byMessageType": counter(m["type"] for m in messages),
        "participants": [
            {"ref": p["ref"], "name": p["name"], "scheme": p["scheme"],
             "messagesSent": p["messagesSent"], "messagesReceived": p["messagesReceived"],
             "threadsCount": p["threadsCount"]}
            for p in sorted(participants, key=lambda x: -(x["messagesSent"] + x["messagesReceived"]))],
        "threads": [
            {"threadId": t["threadId"], "label": t["label"], "messageCount": t["messageCount"],
             "firstMessageAt": t["firstMessageAt"], "lastMessageAt": t["lastMessageAt"]}
            for t in sorted(threads, key=lambda x: -x["messageCount"])],
    })
    print(f"messages={len(messages)} threads={len(threads)} participants={len(participants)} "
          f"activities={len(activities)} totalActivityCost={round(total_cost, 4)} "
          f"errors+warnings={len(problems)}")


if __name__ == "__main__":
    main()
PY
}

# write_manifest — index + security note + reproduction guide for the bundle.
write_manifest() {
    {
        cat <<'MD'
# Spring Voyage — debug bundle

A diagnostic snapshot of a local Podman deployment, collected by
`eng/deploy/scripts/spring-debug-dump.sh`.

> ⚠️ **CONTAINS SECRETS — DO NOT COMMIT OR SHARE AS-IS.** The DB dump includes
> API tokens and credentials; `infra/inspect/*.json` and `host/` include
> environment variables (passwords, keys). A `.gitignore` of `*` is included so
> git ignores the whole bundle by default.

## Layout

| Path | Contents |
|------|----------|
| `db/spring_full_dump.sql.gz` | Complete `pg_dump` (schema + data) — restore with `gunzip -c … \| psql`. |
| `db/spring_schema.sql` | Schema only. |
| `db/row_counts.tsv` | Row count per table (desc). |
| `db/tables/<schema>.<table>.json` | Every table as a JSON array. |
| `db/dapr/state.json` | Dapr actor state store (`public.state`). |
| `db/interactions/raw_snapshot.json` | Atomic snapshot: messages + threads + participants + activities. |
| `db/interactions/messages.jsonl` | One enriched message per line (names resolved; markdown + plain). |
| `db/interactions/threads.json` | Threads = unique participant sets + per-thread aggregates. |
| `db/interactions/participants.json` | Participant directory + sent/received/thread counts. |
| `db/interactions/activities.jsonl` | The activity-event stream (source resolved, per-event cost). |
| `db/interactions/activities-summary.json` | Activity rollup: by type/severity/source, total cost, errors. |
| `db/interactions/summary.json` | Headline counts, time span, direction matrix. |
| `logs/<container>.log` | `podman logs --timestamps` per spring-* container. |
| `infra/` | ps / inspect / stats / info, networks, volumes, images, events timeline. |
| `redis/` | Redis INFO + key inventory + values. |
| `dapr/` | Dapr sidecar metadata API snapshot (best-effort). |
| `host/` | Dispatcher + resolved env from the host state dir. |
| `tools/` | The exact `extract.sql` + `enrich.py` used (re-runnable). |
| `collection.log`, `skipped.txt` | What ran; what was skipped/failed. |

## Reproduce / refresh

```sh
eng/deploy/scripts/spring-debug-dump.sh --output ./spring-debug-dump-new
```

Best run **before** stopping the deployment (the DB section needs Postgres up).
MD
        printf '\n## This run\n\n- Generated: %s (UTC)\n- Host: %s\n- Podman: %s\n' \
            "${TS}" "$(uname -a 2>/dev/null)" "$("${PODMAN}" --version 2>/dev/null)"
        printf -- '- Containers discovered (%s):\n' "${#ALL_CONTAINERS[@]}"
        printf '  - %s\n' "${ALL_CONTAINERS[@]}"
        printf '\n## Skipped / failed steps\n\n'
        if [[ -s "${SKIPS}" ]]; then cat "${SKIPS}"; else echo "(none)"; fi
    } > "${OUT}/MANIFEST.md"
}

mkdir -p "${OUT}/infra/inspect" "${OUT}/logs" "${OUT}/db/tables" "${OUT}/db/interactions" \
         "${OUT}/db/dapr" "${OUT}/redis" "${OUT}/dapr" "${OUT}/host" "${OUT}/tools"

log "spring-debug-dump → ${OUT}"
log "collecting at ${TS} (UTC) with ${PODMAN}"

# Discover every spring-* container (running or exited): platform + runtime
# (persistent/ephemeral/exec/dapr) agents that churn during a loop.
# (while-read, not mapfile — portable back to bash 3.2 on stock macOS.)
ALL_CONTAINERS=()
while IFS= read -r c; do
    [[ -n "${c}" ]] && ALL_CONTAINERS+=("${c}")
done < <(
    { "${PODMAN}" ps -a --format '{{.Names}}' 2>/dev/null | grep -E "${SPRING_PREFIX_RE}";
      printf '%s\n' "${PLATFORM_CONTAINERS[@]}"; } | sort -u
)
log "discovered ${#ALL_CONTAINERS[@]} spring-* containers"

# =====================================================================  INFRA
note "INFRA"
run "podman version"   '"${PODMAN}" version            > "${OUT}/infra/podman-version.txt"'
run "podman info"      '"${PODMAN}" info                > "${OUT}/infra/podman-info.txt"'
run "ps -a (table)"    '"${PODMAN}" ps -a               > "${OUT}/infra/ps.txt"'
run "ps -a (json)"     '"${PODMAN}" ps -a --format json > "${OUT}/infra/ps.json"'
run "images"           '"${PODMAN}" images              > "${OUT}/infra/images.txt"'
run "stats snapshot"   '"${PODMAN}" stats --no-stream --format json > "${OUT}/infra/stats.json"'

run "networks (list)"  '"${PODMAN}" network ls          > "${OUT}/infra/networks.txt"'
while read -r net; do
    [[ -n "${net}" ]] || continue
    run "network inspect ${net}" '"${PODMAN}" network inspect "'"${net}"'" > "${OUT}/infra/network-'"${net}"'.json"'
done < <("${PODMAN}" network ls --format '{{.Name}}' 2>/dev/null | grep -E "${SPRING_PREFIX_RE}")

run "volumes (list)"   '"${PODMAN}" volume ls           > "${OUT}/infra/volumes.txt"'
while read -r vol; do
    [[ -n "${vol}" ]] || continue
    run "volume inspect ${vol}" '"${PODMAN}" volume inspect "'"${vol}"'" > "${OUT}/infra/volume-'"${vol}"'.json"'
done < <("${PODMAN}" volume ls --format '{{.Name}}' 2>/dev/null | grep -E "${SPRING_PREFIX_RE}")

# Bounded events timeline (restart/start/die events explain container churn).
# --stream=false makes it print the window and exit (non-blocking).
run "podman events (last 6h)" '"${PODMAN}" events --since 6h --stream=false > "${OUT}/infra/events.txt"'

# Per-container inspect (restart policy, restart count, mounts, env, health, exit code).
for c in "${ALL_CONTAINERS[@]}"; do
    container_exists "${c}" || { printf -- '- inspect %s (absent)\n' "${c}" >>"${SKIPS}"; continue; }
    run "inspect ${c}" '"${PODMAN}" inspect "'"${c}"'" > "${OUT}/infra/inspect/'"${c}"'.json"'
done

# =====================================================================  LOGS
if [[ "${DO_LOGS}" -eq 1 ]]; then
    note "LOGS"
    for c in "${ALL_CONTAINERS[@]}"; do
        container_exists "${c}" || { printf -- '- logs %s (absent)\n' "${c}" >>"${SKIPS}"; continue; }
        run "logs ${c}" '"${PODMAN}" logs --timestamps "'"${c}"'" > "${OUT}/logs/'"${c}"'.log" 2>&1'
    done
fi

# =====================================================================  DATABASE
if [[ "${DO_DB}" -eq 1 ]] && container_running "${PG_CONTAINER}"; then
    note "DATABASE"
    PGUSER="$("${PODMAN}" exec "${PG_CONTAINER}" printenv POSTGRES_USER 2>/dev/null || echo spring)"
    PGDB="$("${PODMAN}" exec "${PG_CONTAINER}" printenv POSTGRES_DB 2>/dev/null || echo spring)"
    log "postgres: container=${PG_CONTAINER} user=${PGUSER} db=${PGDB}"

    run "pg_dump (schema+data, gz)" \
        '"${PODMAN}" exec "${PG_CONTAINER}" pg_dump -U "${PGUSER}" -d "${PGDB}" --no-owner --no-privileges | gzip > "${OUT}/db/spring_full_dump.sql.gz"'
    run "pg_dump (schema only)" \
        '"${PODMAN}" exec "${PG_CONTAINER}" pg_dump -U "${PGUSER}" -d "${PGDB}" --schema-only --no-owner --no-privileges > "${OUT}/db/spring_schema.sql"'
    # Per-table JSON for every table in spring + public (skip oversized ones);
    # exact row counts accumulate into row_counts.tsv as we go.
    : > "${OUT}/db/row_counts.tsv"
    while IFS=$'\t' read -r schema table; do
        [[ -n "${schema}" ]] || continue
        rows="$(psql_q -At -c "SELECT count(*) FROM \"${schema}\".\"${table}\"" 2>/dev/null || echo 0)"
        printf '%s\t%s.%s\n' "${rows}" "${schema}" "${table}" >> "${OUT}/db/row_counts.tsv"
        if [[ "${rows}" =~ ^[0-9]+$ ]] && [[ "${rows}" -gt "${MAX_ROWS}" ]]; then
            printf -- '- table %s.%s JSON (rows=%s > max-rows=%s; in pg_dump)\n' "${schema}" "${table}" "${rows}" "${MAX_ROWS}" >>"${SKIPS}"
            continue
        fi
        table_json "${schema}" "${table}" "${OUT}/db/tables/${schema}.${table}.json"
    done < <(psql_q -At -F $'\t' -c "SELECT table_schema, table_name FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema IN ('spring','public') ORDER BY 1,2")
    run "sort row counts" 'sort -rn "${OUT}/db/row_counts.tsv" -o "${OUT}/db/row_counts.tsv"'

    # Dapr actor state store (the public.state table) — actor/mailbox state.
    table_json public state "${OUT}/db/dapr/state.json"

    # ---- enriched, human-readable interaction + activity exports -----------
    write_extract_sql
    write_enrich_py
    if run "extract interactions+activities" \
            'psql_in -At < "${OUT}/tools/extract.sql" > "${OUT}/db/interactions/raw_snapshot.json"'; then
        if command -v python3 >/dev/null 2>&1; then
            run "enrich exports" 'python3 "${OUT}/tools/enrich.py" "${OUT}/db/interactions/raw_snapshot.json" "${OUT}/db/interactions"'
        else
            log "python3 not found — keeping raw_snapshot.json, skipping enriched exports"
            printf -- '- enriched exports (python3 missing)\n' >>"${SKIPS}"
        fi
    fi
else
    [[ "${DO_DB}" -eq 1 ]] && log "postgres '${PG_CONTAINER}' not running — skipping DATABASE section"
    [[ "${DO_DB}" -eq 1 ]] && printf -- '- DATABASE section (postgres not running)\n' >>"${SKIPS}"
fi

# =====================================================================  REDIS
if container_running "${REDIS_CONTAINER}"; then
    note "REDIS"
    run "redis info"   '"${PODMAN}" exec "${REDIS_CONTAINER}" redis-cli INFO   > "${OUT}/redis/info.txt"'
    run "redis dbsize" '"${PODMAN}" exec "${REDIS_CONTAINER}" redis-cli DBSIZE > "${OUT}/redis/dbsize.txt"'
    run "redis keys"   '"${PODMAN}" exec "${REDIS_CONTAINER}" redis-cli --scan > "${OUT}/redis/keys.txt"'
    # Dump each key's type + value inside ONE exec (a per-key podman exec would be
    # pathologically slow). Dapr actor reminders / state surface here. Bounded.
    cat > "${OUT}/tools/redis-dump.sh" <<'RSH'
#!/bin/sh
redis-cli --scan | head -n 5000 | while IFS= read -r k; do
    [ -n "$k" ] || continue
    ty=$(redis-cli type "$k")
    printf '### %s [%s]\n' "$k" "$ty"
    case "$ty" in
        hash)   redis-cli hgetall "$k" ;;
        list)   redis-cli lrange "$k" 0 -1 ;;
        set)    redis-cli smembers "$k" ;;
        zset)   redis-cli zrange "$k" 0 -1 withscores ;;
        stream) redis-cli xrange "$k" - + ;;
        *)      redis-cli get "$k" ;;
    esac
    printf '\n'
done
RSH
    run "redis values" '"${PODMAN}" exec -i "${REDIS_CONTAINER}" sh < "${OUT}/tools/redis-dump.sh" > "${OUT}/redis/values.txt" 2>&1'
else
    log "redis '${REDIS_CONTAINER}' not running — skipping REDIS section"
    printf -- '- REDIS section (redis not running)\n' >>"${SKIPS}"
fi

# =====================================================================  DAPR
note "DAPR"
# Best-effort sidecar metadata API snapshot (registered actors, components).
for app in spring-api spring-worker; do
    container_running "${app}" || continue
    run "dapr metadata ${app}" '
        "${PODMAN}" exec "'"${app}"'" sh -c '"'"'command -v curl >/dev/null 2>&1 && curl -fsS http://127.0.0.1:3500/v1.0/metadata || (command -v wget >/dev/null 2>&1 && wget -qO- http://127.0.0.1:3500/v1.0/metadata)'"'"' > "${OUT}/dapr/metadata-'"${app}"'.json"'
done

# =====================================================================  HOST STATE
note "HOST STATE"
if [[ -d "${HOST_STATE_DIR}" ]]; then
    run "host state listing" 'ls -laR "${HOST_STATE_DIR}" > "${OUT}/host/state-listing.txt"'
    # Copy small text/env/log files (skip anything large or binary-ish).
    run "host state copy" '
        cd "${HOST_STATE_DIR}" && find . -type f \( -name "*.env" -o -name "*.log" -o -name "*.json" -o -name "*.yaml" -o -name "*.yml" -o -name "*.txt" \) -size -2M -print0 \
          | while IFS= read -r -d "" f; do d="${OUT}/host/files/$(dirname "$f")"; mkdir -p "$d"; cp "$f" "$d/"; done'
else
    log "host state dir ${HOST_STATE_DIR} not present — skipping"
    printf -- '- HOST STATE (%s absent)\n' "${HOST_STATE_DIR}" >>"${SKIPS}"
fi

# =====================================================================  MANIFEST
note "MANIFEST"
write_manifest

# =====================================================================  ARCHIVE
if [[ "${DO_ARCHIVE}" -eq 1 ]]; then
    ARCHIVE="${OUT}.tar.gz"
    if run "archive" 'tar -czf "${ARCHIVE}" -C "$(dirname "${OUT}")" "$(basename "${OUT}")"'; then
        log "archive → ${ARCHIVE}"
    fi
fi

SKIP_N=$(grep -c . "${SKIPS}" 2>/dev/null || echo 0)
log "done. bundle: ${OUT}  (skipped/failed steps: ${SKIP_N} — see skipped.txt)"
exit 0
