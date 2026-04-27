#!/usr/bin/env bash
# Conversation state lifecycle observability (#404).
#
# When a Domain message with a fresh ConversationId arrives at an idle agent,
# AgentActor.HandleDomainMessageAsync emits three activity events in order:
#   1. MessageReceived      — from ReceiveAsync, before any state mutation.
#   2. ConversationStarted  — once the ConversationChannel is persisted.
#   3. StateChanged         — "Idle → Active" once the dispatch task is armed.
#
# This scenario verifies those three lifecycle events reach the activity
# query store. The actual LLM-backed turn that would flip the agent back to
# Idle (ConversationCompleted) is out of scope for the fast pool — the three
# upstream events alone prove the conversation state machine kicks off
# correctly.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

agent="$(e2e::agent_name conv-lifecycle)"
unit="$(e2e::unit_name conv-lifecycle-unit)"
conv_id="${E2E_PREFIX}-${E2E_RUN_ID}-conv-lc"

trap 'e2e::cleanup_unit "${unit}"; e2e::cleanup_agent "${agent}"' EXIT

# --- Setup -------------------------------------------------------------------
# #744 requires every agent to be born into ≥1 unit — create a throwaway
# carrier unit first.
e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "carrier unit create succeeds"

e2e::log "spring agent create ${agent} --unit ${unit}"
response="$(e2e::cli_agent_create --output json "${agent}" --unit "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "agent create succeeds"

agent_id="$(printf '%s' "${body}" | awk -F'"' '/"id":/ { print $4; exit }')"
if [[ -z "${agent_id}" ]]; then
    e2e::fail "could not extract agent id from create response"
    e2e::summary
    exit 1
fi
expected_source="agent:${agent_id}"

# --- Kick off a fresh conversation ------------------------------------------
# Raw HTTP — the CLI currently crashes on the server's 502 path when no
# execution tool is configured (see 13-agent-domain-message.sh). The message
# is delivered either way; only the downstream dispatcher tail may 502.
payload=$(cat <<EOF
{"to":{"scheme":"agent","path":"${agent}"},"type":"Domain","conversationId":"${conv_id}","payload":"kickoff"}
EOF
)
e2e::log "POST /api/v1/messages (Domain → agent://${agent}, conv=${conv_id})"
response="$(e2e::http POST /api/v1/messages "${payload}")"
status="${response##*$'\n'}"
# Accept 200 or 502 for the same reason as 13-agent-domain-message: the
# ConversationStarted and StateChanged events are emitted BEFORE the
# dispatcher runs, so a 502 from dispatch does not invalidate the check.
if [[ "${status}" == "200" || "${status}" == "502" ]]; then
    e2e::ok "message POST reached the actor (status ${status})"
else
    e2e::fail "unexpected message POST status — got ${status}"
fi

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

# 1. MessageReceived — happens first inside AgentActor.ReceiveAsync.
if msg_body="$(poll_for_event_type MessageReceived)"; then
    e2e::ok "conversation lifecycle: MessageReceived event recorded"
else
    e2e::fail "MessageReceived never surfaced within 10s: ${msg_body:0:400}"
fi

# 2. ConversationStarted — happens once the ConversationChannel is persisted.
# The event summary embeds the conversation id; assert both that the event
# fires AND that it carries our run-scoped correlation id so we know it was
# triggered by this scenario and not leaked from an earlier run.
if conv_body="$(poll_for_event_type ConversationStarted)"; then
    e2e::ok "conversation lifecycle: ConversationStarted event recorded"
    e2e::expect_contains "${conv_id}" "${conv_body}" "ConversationStarted carries this scenario's conversation id"
else
    e2e::fail "ConversationStarted never surfaced within 10s: ${conv_body:0:400}"
fi

# 3. StateChanged — "Idle → Active" when the dispatch task is armed.
if state_body="$(poll_for_event_type StateChanged)"; then
    e2e::ok "conversation lifecycle: StateChanged event recorded"
    e2e::expect_contains "Idle to Active" "${state_body}" "StateChanged carries the Idle→Active transition"
else
    e2e::fail "StateChanged never surfaced within 10s: ${state_body:0:400}"
fi

e2e::summary
