#!/usr/bin/env bash
# pool: fast
# Thread state lifecycle observability (#404).
#
# When a Domain message with a fresh ThreadId arrives at an idle agent,
# AgentActor.HandleDomainMessageAsync emits three activity events in order:
#   1. MessageArrived  — from ReceiveAsync, before any state mutation.
#   2. ThreadStarted    — once the ThreadChannel is persisted.
#   3. StateChanged     — "Idle → Active" once the dispatch task is armed.
#
# This scenario verifies those three lifecycle events reach the activity
# query store. The actual LLM-backed turn is out of scope for the fast pool —
# the three upstream events alone prove the thread state machine kicks off
# correctly.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

agent="$(e2e::agent_name conv-lifecycle)"
unit="$(e2e::unit_name conv-lifecycle-unit)"
# The server validates threadId as a Guid now; the previous human-shaped id
# returns 400. Omit the field on the POST and capture the server-generated
# canonical hex thread id from the response below.
thread_id=""

trap 'e2e::cleanup_unit "${unit}"; e2e::cleanup_agent "${agent}"' EXIT

# --- Setup -------------------------------------------------------------------
# #744 requires every agent to be born into ≥1 unit — create a throwaway
# carrier unit first.
e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
unit_body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "carrier unit create succeeds"
# Canonical hex id of the carrier unit — needed to grant the caller's Hat.
unit_id="$(printf '%s' "${unit_body}" | awk -F'"' '/"name":/ { print $4; exit }')"

e2e::log "spring agent create --name ${agent} --unit ${unit}"
response="$(e2e::cli_agent_create --output json --name "${agent}" --unit "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "agent create succeeds"

agent_id="$(printf '%s' "${body}" | awk -F'"' '/"id":/ { print $4; exit }')"
if [[ -z "${agent_id}" ]]; then
    e2e::fail "could not extract agent id from create response"
    e2e::summary
    exit 1
fi
# Canonical hex form (no dashes) — the API rejects display names and dashed
# Guids in `to.path` now.
agent_id_hex="${agent_id//-/}"
expected_source="agent:${agent_id_hex}"

# #2972 (commit c3f92950): the tenant-user → agent Domain send is gated on Hat
# reachability — the caller must wear a Hat that is a human member of a unit
# the agent sits in. Grant the caller's own Hat owner membership on the
# carrier unit so the kickoff POST reaches the actor (and emits the lifecycle
# events) instead of being rejected 403 NoReachableHat.
e2e::add_caller_hat "${unit_id}"

# --- Kick off a fresh thread -------------------------------------------------
# Raw HTTP — the CLI currently crashes on the server's 502 path when no
# execution tool is configured (see 13-agent-domain-message.sh). The message
# is delivered either way; only the downstream dispatcher tail may 502.
#
# Both `to.path` and `threadId` must be canonical hex Guids; threadId is
# omitted here and captured from the response, which gives us a real Guid
# for the ThreadStarted assertion below.
payload=$(cat <<EOF
{"to":{"scheme":"agent","path":"${agent_id_hex}"},"type":"Domain","payload":"kickoff"}
EOF
)
e2e::log "POST /api/v1/tenant/messages (Domain → agent:${agent_id_hex})"
response="$(e2e::http POST /api/v1/tenant/messages "${payload}")"
status="${response##*$'\n'}"
post_body="${response%$'\n'*}"
# Accept 200 or 502 for the same reason as 13-agent-domain-message: the
# ThreadStarted and StateChanged events are emitted BEFORE the dispatcher
# runs, so a 502 from dispatch does not invalidate the check.
if [[ "${status}" == "200" || "${status}" == "502" ]]; then
    e2e::ok "message POST reached the actor (status ${status})"
else
    e2e::fail "unexpected message POST status — got ${status}"
fi
# Capture the server-allocated threadId so the ThreadStarted assertion can
# verify the event carries this scenario's correlation id (and not noise
# from another run leaking through). awk-by-line doesn't work here because
# the response is a single JSON line; use grep -o to extract the field.
thread_id="$(printf '%s' "${post_body}" | grep -o '"threadId":"[^"]*"' | head -1 | sed 's/"threadId":"\([^"]*\)"/\1/')"

# --- Poll the activity store for each expected lifecycle event --------------
# Persister batches every second; retry until all three events appear or we
# give up after ~10s.
poll_for_event_type() {
    local event_type="$1" attempt query_response query_status query_body
    for attempt in 1 2 3 4 5 6 7 8 9 10; do
        query_response="$(e2e::http GET "/api/v1/tenant/activity?source=${expected_source}&eventType=${event_type}&limit=5")"
        query_status="${query_response##*$'\n'}"
        query_body="${query_response%$'\n'*}"
        if [[ "${query_status}" == "200" ]] && [[ "${query_body}" == *"${event_type}"* ]]; then
            printf '%s' "${query_body}"
            return 0
        fi
        sleep 1
    done
    printf '%s' "${query_body}"
    return 1
}

# 1. MessageArrived — happens first inside AgentActor.ReceiveAsync.
if msg_body="$(poll_for_event_type MessageArrived)"; then
    e2e::ok "thread lifecycle: MessageArrived event recorded"
else
    e2e::fail "MessageArrived never surfaced within 10s: ${msg_body:0:400}"
fi

# 2. ThreadStarted — happens once the ThreadChannel is persisted.
# The event summary embeds the thread id; assert both that the event
# fires AND that it carries our run-scoped correlation id so we know it was
# triggered by this scenario and not leaked from an earlier run.
if thread_body="$(poll_for_event_type ThreadStarted)"; then
    e2e::ok "thread lifecycle: ThreadStarted event recorded"
    e2e::expect_contains "${thread_id}" "${thread_body}" "ThreadStarted carries this scenario's thread id"
else
    e2e::fail "ThreadStarted never surfaced within 10s: ${thread_body:0:400}"
fi

# 3. Dispatch tail — proves the message reached the dispatcher. The agent now
# emits a StateChanged "…created" event at registration that ALSO matches
# source+eventType, so a bare StateChanged poll matches that instead of a
# transition; look specifically for the "Idle → Active" summary. In the fast
# pool the agent has no execution config, so the dispatcher runs
# MessageDispatchedToRuntime → RuntimeStarted → ErrorOccurred and never reaches
# Idle→Active. Accept either the transition (happy path, covered end-to-end by
# the llm-pool turn scenario) or ErrorOccurred (fast-pool default) as proof the
# dispatch tail kicked in. Check both each iteration so the deterministic
# ErrorOccurred path doesn't wait out the full window on the absent transition.
tail_ok=0
tail_kind=""
for _ in 1 2 3 4 5 6 7 8 9 10; do
    sc_resp="$(e2e::http GET "/api/v1/tenant/activity?source=${expected_source}&eventType=StateChanged&limit=20")"
    if [[ "${sc_resp%$'\n'*}" == *"Idle to Active"* ]]; then
        tail_ok=1; tail_kind="StateChanged Idle→Active"; break
    fi
    err_resp="$(e2e::http GET "/api/v1/tenant/activity?source=${expected_source}&eventType=ErrorOccurred&limit=5")"
    if [[ "${err_resp%$'\n'*}" == *"ErrorOccurred"* ]]; then
        tail_ok=1; tail_kind="ErrorOccurred (fast-pool default)"; break
    fi
    sleep 1
done
if (( tail_ok == 1 )); then
    e2e::ok "thread lifecycle: dispatch tail recorded — ${tail_kind}"
else
    e2e::fail "neither StateChanged Idle→Active nor ErrorOccurred surfaced within 10s"
fi

e2e::summary
