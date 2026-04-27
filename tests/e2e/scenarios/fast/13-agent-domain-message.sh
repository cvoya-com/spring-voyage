#!/usr/bin/env bash
# A2A / human-to-agent messaging observability (#404).
#
# Exercises the message plumbing end-to-end without an LLM backend: create an
# agent, send a Domain message via POST /api/v1/tenant/messages, and assert the
# receiver-side `MessageReceived` activity event lands in the activity query
# store. This covers the wiring that every real agent-to-agent or
# human-to-agent path depends on (message routing → actor dispatch →
# activity-bus publish → persister).
#
# The scenario deliberately DOES NOT require the dispatch to succeed.
# Without `execution.tool` configured on the agent, the downstream dispatcher
# emits `ErrorOccurred` and HTTP may surface a 502; that is fine. The
# assertion is that the upstream MessageReceived event persisted. The full
# agent turn (MessageReceived → dispatcher runs → agent response) lives in
# the LLM-backed scenario pool (see #330/#334) because the dispatch tail
# needs a real execution tool.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

agent="$(e2e::agent_name message-target)"
unit="$(e2e::unit_name message-target-unit)"
conv_id="${E2E_PREFIX}-${E2E_RUN_ID}-conv-msg"

# Cascading unit purge drops the agent's membership row before the agent
# purge runs.
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

# Activity source filtering uses `agent:<actorId>` (the GUID) — fish it out
# of the CLI create response so we can filter the activity query precisely.
# The response JSON is { "id": "<guid>", "name": "<path>", ... }.
agent_id="$(printf '%s' "${body}" | awk -F'"' '/"id":/ { print $4; exit }')"
if [[ -z "${agent_id}" ]]; then
    e2e::fail "could not extract agent id from create response: ${body:0:200}"
    e2e::summary
    exit 1
fi
e2e::log "agent actor id: ${agent_id}"

# --- Send a Domain message ---------------------------------------------------
# Driven through raw HTTP rather than `spring message send` because the CLI
# currently crashes on the server's 502 response (dispatch error without an
# execution tool) before returning. The message itself is delivered and the
# activity event persists either way.
payload=$(cat <<EOF
{"to":{"scheme":"agent","path":"${agent}"},"type":"Domain","conversationId":"${conv_id}","payload":"hello"}
EOF
)
e2e::log "POST /api/v1/tenant/messages (Domain → agent://${agent}, conv=${conv_id})"
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

# --- Poll the activity query store for MessageReceived ------------------------
# Persister batches every second (ActivityEventPersister), so a single query
# right after the send races. Retry up to ~10s with a short sleep.
expected_source="agent:${agent_id}"
found=0
for attempt in 1 2 3 4 5 6 7 8 9 10; do
    query_response="$(e2e::http GET "/api/v1/tenant/activity?source=${expected_source}&eventType=MessageReceived&limit=5")"
    query_status="${query_response##*$'\n'}"
    query_body="${query_response%$'\n'*}"
    if [[ "${query_status}" == "200" ]] && [[ "${query_body}" == *"MessageReceived"* ]]; then
        found=1
        break
    fi
    sleep 1
done

if (( found == 1 )); then
    e2e::ok "activity query returns MessageReceived for source=${expected_source}"
else
    e2e::fail "no MessageReceived event surfaced for source=${expected_source} within 10s: ${query_body:0:400}"
fi

e2e::summary
