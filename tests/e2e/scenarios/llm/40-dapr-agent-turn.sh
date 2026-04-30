#!/usr/bin/env bash
# LLM scenario: Dapr Agent turn via A2A protocol.
#
# Creates a unit + agent definition with execution.tool=dapr-agent, dispatches a
# simple message, and verifies the round-trip produces a non-empty, non-error
# LLM response. This is the real gate on the Ollama-driven agent runtime
# (closes #480): the previous smoke test asserted only that the HTTP call did
# not 5xx, which silently passed when the executor fell through to
# TaskState.failed. The executor's failure path returns a 200 with an error
# artefact, so we explicitly check the artefact content.
#
# Scope: gated on e2e::require_ollama so the base scenario set stays green on
# hosts without a reachable Ollama.
#
# Seeding path: we persist `execution.tool=dapr-agent` on the agent definition
# via `spring agent create --definition`. That knob is what tells
# A2AExecutionDispatcher to route through DaprAgentLauncher. Without it the
# dispatcher throws "Agent has no execution configuration".
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

if ! e2e::require_ollama; then
    e2e::log "skipping: Ollama not reachable"
    exit 0
fi

unit="$(e2e::unit_name llm-dapr-agent)"
agent="$(e2e::agent_name llm-dapr-agent)"
image="${SPRING_DAPR_AGENT_IMAGE:-localhost/spring-voyage-agent-dapr:latest}"
model="${SPRING_DAPR_AGENT_MODEL:-llama3.2:3b}"
provider="${SPRING_DAPR_AGENT_PROVIDER:-ollama}"

cleanup() {
    e2e::cli unit purge "${unit}" --confirm >/dev/null 2>&1 || true
    e2e::cli agent purge "${agent}" --confirm >/dev/null 2>&1 || true
}
trap cleanup EXIT

# --- Setup --------------------------------------------------------------------
e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create succeeds"

# Inline JSON body for `--definition`. Uses jq when available (clean escaping)
# and falls back to a hand-written literal otherwise so the scenario runs on
# hosts without jq.
if command -v jq >/dev/null 2>&1; then
    definition="$(jq -cn \
        --arg tool "dapr-agent" \
        --arg image "${image}" \
        --arg provider "${provider}" \
        --arg model "${model}" \
        '{execution: {tool: $tool, image: $image, provider: $provider, model: $model}}')"
else
    definition="{\"execution\":{\"tool\":\"dapr-agent\",\"image\":\"${image}\",\"provider\":\"${provider}\",\"model\":\"${model}\"}}"
fi


# #744: agent create requires --unit; the membership is registered atomically.
e2e::log "spring agent create ${agent} --unit ${unit} (tool=dapr-agent, provider=${provider}, model=${model})"
response="$(e2e::cli_agent_create --output json "${agent}" \
    --unit "${unit}" \
    --name "Dapr Agent" \
    --definition "${definition}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "agent create with dapr-agent execution succeeds"

# --- Dispatch a turn ----------------------------------------------------------
# The send itself returns as soon as the message is accepted. The actual agent
# turn runs async — we then tail the thread until an agent reply appears
# (or we hit the timeout, which itself is a failure signal because the turn
# should complete in under a minute for a trivial prompt on llama3.2:3b).
# `--conversation` was renamed to `--thread` when the conversation surface was
# unified into the `thread` subcommand.
thread_id="e2e-thread-$(date +%s)-$$"
e2e::log "spring message send agent://${agent} (thread=${thread_id})"
response="$(e2e::cli --output json message send "agent://${agent}" \
    "Say the word 'hello' in a single sentence and nothing else." \
    --thread "${thread_id}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "message send succeeds"
e2e::expect_contains "messageId" "${body}" "send response carries a messageId"

# --- Assert on the LLM's actual reply -----------------------------------------
# Poll `thread show` (up to the timeout) for an event sourced from the agent.
# `conversation show` was renamed to `thread show` when the conversation surface
# was unified into the `thread` subcommand.
# A TaskState.failed payload surfaces as an event whose summary carries
# "Error:"; we flag that explicitly so the scenario fails loud instead of
# silently passing the way the old smoke test did (#480 finding 5).
timeout="${SPRING_DAPR_AGENT_TURN_TIMEOUT:-180}"
deadline=$(( $(date +%s) + timeout ))
agent_reply=""
last_show=""
while (( $(date +%s) < deadline )); do
    show_raw="$(e2e::cli --output json thread show "${thread_id}" 2>&1)" || true
    show_code="${show_raw##*$'\n'}"
    show_body="${show_raw%$'\n'*}"
    last_show="${show_body}"

    if [[ "${show_code}" != "0" ]]; then
        sleep 2
        continue
    fi

    # Any event whose source starts with "agent://" represents output from the
    # executor. `jq` is the precise extractor; `grep` is a coarser fallback.
    if command -v jq >/dev/null 2>&1; then
        agent_reply="$(printf '%s' "${show_body}" \
            | jq -r '[.events[]? | select(.source|tostring|startswith("agent://"))][-1].summary // ""' \
            2>/dev/null || true)"
    else
        agent_reply="$(printf '%s' "${show_body}" \
            | grep -oE '"summary":[[:space:]]*"[^"]+"' | tail -1 \
            | sed -E 's/^"summary":[[:space:]]*"//; s/"$//' || true)"
    fi

    if [[ -n "${agent_reply}" ]]; then
        break
    fi

    sleep 2
done

if [[ -z "${agent_reply}" ]]; then
    e2e::fail "no agent reply surfaced in thread ${thread_id} within ${timeout}s (last show body: ${last_show:0:500})"
else
    case "${agent_reply}" in
        *[Ee]rror:*|*TaskState.failed*)
            e2e::fail "agent reply carries an error (TaskState.failed or Error:…): ${agent_reply:0:500}"
            ;;
        *)
            e2e::ok "agent reply is non-empty and non-error (${#agent_reply} chars)"
            ;;
    esac
fi

e2e::log "agent reply: ${agent_reply:0:500}"
e2e::summary
