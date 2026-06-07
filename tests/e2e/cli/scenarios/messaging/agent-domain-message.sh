#!/usr/bin/env bash
# pool: fast
# A2A / human-to-agent messaging observability (#404).
#
# Exercises the message plumbing end-to-end without an LLM backend: create an
# agent, send a Domain message via POST /api/v1/tenant/messages, and assert the
# receiver-side `MessageArrived` activity event lands in the activity query
# store. This covers the wiring that every real agent-to-agent or
# human-to-agent path depends on (message routing → actor dispatch →
# activity-bus publish → persister).
#
# The scenario deliberately DOES NOT require the dispatch to succeed.
# Without `execution.tool` configured on the agent, the downstream dispatcher
# emits `ErrorOccurred` and HTTP may surface a 502; that is fine. The
# assertion is that the upstream MessageArrived event persisted.
#
# The full dispatch round-trip (dispatcher → agent JSON-RPC over A2A → agent
# response back into the timeline) is now guarded by the integration test
# `A2ADispatchTransportContractTests` (issue #1465) which stands up an
# in-process JSON-RPC responder and asserts the .NET A2AClient can reach
# `message/send` at `/`. That test runs on every PR via the integration
# suite. The LLM-backed end-to-end (with a real LLM in the loop) lives in
# `scenarios/agents/spring-voyage-agent-turn.sh` and runs in the on-demand LLM lane
# (see `tests/e2e/cli/README.md` for the pool conventions).
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

agent="$(e2e::agent_name message-target)"
unit="$(e2e::unit_name message-target-unit)"
# The server validates threadId as a Guid now; the previous human-shaped id
# returns 400. Letting the server allocate one (omit the field) yields a
# canonical 32-char hex thread id back via the response.
thread_id=""

# Cascading unit purge drops the agent's membership row before the agent
# purge runs.
trap 'e2e::cleanup_unit "${unit}"; e2e::cleanup_agent "${agent}"' EXIT

# --- Setup -------------------------------------------------------------------
# #744 requires every agent to be born into ≥1 unit — create a throwaway
# carrier unit first.
e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
unit_body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "carrier unit create succeeds"
# Canonical hex id of the carrier unit — needed to grant the caller's Hat
# membership below (and for any raw-HTTP unit calls).
unit_id="$(printf '%s' "${unit_body}" | awk -F'"' '/"name":/ { print $4; exit }')"

e2e::log "spring agent create --name ${agent} --unit ${unit}"
response="$(e2e::cli_agent_create --output json --name "${agent}" --unit "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "agent create succeeds"

# Activity source filtering uses `agent:<actorId>` — the actorId is the
# canonical hex (no-dashes) Guid form. AgentResponse keeps `name` = display
# name today, so strip dashes off the `id` field for the path-id form.
agent_id="$(printf '%s' "${body}" | awk -F'"' '/"id":/ { print $4; exit }')"
if [[ -z "${agent_id}" ]]; then
    e2e::fail "could not extract agent id from create response: ${body:0:200}"
    e2e::summary
    exit 1
fi
agent_id_hex="${agent_id//-/}"
e2e::log "agent actor id: ${agent_id} (hex ${agent_id_hex})"

# #2972 (commit c3f92950): a tenant-user → agent Domain send is gated on Hat
# reachability — the caller must wear a Hat that is a human member of a unit
# the agent sits in. The carrier unit has no human members yet, so grant the
# caller's own Hat owner membership on it; otherwise the POST below is
# rejected 403 NoReachableHat before the actor (and thus MessageArrived) is
# ever reached.
e2e::add_caller_hat "${unit_id}"

# --- Send a Domain message ---------------------------------------------------
# Driven through raw HTTP rather than `spring message send` because the
# server may surface a 502 here (dispatch error without an execution tool)
# and we want to assert on the persisted MessageArrived event regardless
# of the dispatch tail. The actual JSON-RPC dispatch path is covered by
# `A2ADispatchTransportContractTests` (#1465).
#
# The `to.path` field must carry the canonical hex form; the API rejects
# the human display name with a Guid-parse 400/500. The CLI surface that
# accepts names goes through CliResolver — this raw HTTP path does not.
payload=$(cat <<EOF
{"to":{"scheme":"agent","path":"${agent_id_hex}"},"type":"Domain","payload":"hello"}
EOF
)
e2e::log "POST /api/v1/tenant/messages (Domain → agent:${agent_id_hex})"
response="$(e2e::http POST /api/v1/tenant/messages "${payload}")"
status="${response##*$'\n'}"
# The server may respond 200 (dispatch chained cleanly) or 502 (dispatch
# tail failed because no execution tool is configured); either outcome means
# the message hit the actor. 4xx/5xx other than 502 would indicate a
# routing or auth regression we care about.
if [[ "${status}" == "200" || "${status}" == "502" ]]; then
    e2e::ok "message POST reached the actor (status ${status})"
else
    e2e::fail "unexpected message POST status — got ${status}"
fi

# --- Poll the activity query store for MessageArrived ------------------------
# Persister batches every second (ActivityEventPersister), so a single query
# right after the send races. Retry up to ~10s with a short sleep.
expected_source="agent:${agent_id_hex}"
found=0
for attempt in 1 2 3 4 5 6 7 8 9 10; do
    query_response="$(e2e::http GET "/api/v1/tenant/activity?source=${expected_source}&eventType=MessageArrived&limit=5")"
    query_status="${query_response##*$'\n'}"
    query_body="${query_response%$'\n'*}"
    if [[ "${query_status}" == "200" ]] && [[ "${query_body}" == *"MessageArrived"* ]]; then
        found=1
        break
    fi
    sleep 1
done

if (( found == 1 )); then
    e2e::ok "activity query returns MessageArrived for source=${expected_source}"
else
    e2e::fail "no MessageArrived event surfaced for source=${expected_source} within 10s: ${query_body:0:400}"
fi

e2e::summary
