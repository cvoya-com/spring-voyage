#!/usr/bin/env bash
# pool: llm
# Multi-turn conversation against a Dapr Agent (Ollama backend).
#
# Builds a unit + dapr-agent agent from scratch, then sends three messages
# in the same thread and verifies each turn produces an agent reply. This
# covers the "create a unit from scratch, add an agent, communicate for a
# few turns" flow the user explicitly asked about.
#
# Each turn:
#   1. Send a Domain message to the agent on a single thread id.
#   2. Wait until a NEW agent reply (one we haven't seen before) appears.
#   3. Capture the body and proceed to the next turn.
#
# Why a single thread id: the dapr-agent runtime keeps per-thread context
# in its state store, so subsequent turns see the prior history. Each turn
# is verified against the count of agent-authored events growing.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

if ! e2e::require_ollama; then
    e2e::log "skipping: Ollama not reachable"
    exit 0
fi

unit="$(e2e::unit_name multi-turn)"
agent="$(e2e::agent_name multi-turn)"
image="${SPRING_DAPR_AGENT_IMAGE:-localhost/spring-voyage-agent-dapr:latest}"
model="${SPRING_DAPR_AGENT_MODEL:-llama3.2:3b}"
provider="${SPRING_DAPR_AGENT_PROVIDER:-ollama}"
thread_id="e2e-multi-turn-$(date +%s)-$$"
turn_timeout="${SPRING_MULTI_TURN_TIMEOUT:-180}"

cleanup() {
    e2e::cli unit purge "${unit}" --confirm >/dev/null 2>&1 || true
    e2e::cli agent purge "${agent}" --confirm >/dev/null 2>&1 || true
}
trap cleanup EXIT

# --- Setup ------------------------------------------------------------------
e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create succeeds"

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

e2e::log "spring agent create ${agent} --unit ${unit} (tool=dapr-agent)"
response="$(e2e::cli_agent_create --output json "${agent}" \
    --unit "${unit}" \
    --name "Multi-turn Agent" \
    --definition "${definition}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "agent create succeeds"

# --- Helper: wait for the (turn_index)th agent reply -------------------------
# Counts events whose summary contains "from agent:" — these are the
# inbound messages the human inbox receives from the agent. We wait until
# the count reaches at least target_count, then return the latest body.
wait_for_agent_reply() {
    local target_count="$1"
    local deadline=$(( $(date +%s) + turn_timeout ))
    local body=""
    while (( $(date +%s) < deadline )); do
        local show_raw show_code show_body
        show_raw="$(e2e::cli --output json thread show "${thread_id}" 2>&1)" || true
        show_code="${show_raw##*$'\n'}"
        show_body="${show_raw%$'\n'*}"

        if [[ "${show_code}" != "0" ]]; then
            sleep 2
            continue
        fi

        local count
        if command -v jq >/dev/null 2>&1; then
            count="$(printf '%s' "${show_body}" \
                | jq '[.events[]? | select(.eventType == "MessageReceived" and ((.summary // "") | test("from agent:")))] | length' \
                2>/dev/null || echo 0)"
        else
            count="$(printf '%s' "${show_body}" | grep -c 'from agent:' || true)"
        fi
        count="${count:-0}"

        if (( count >= target_count )); then
            if command -v jq >/dev/null 2>&1; then
                body="$(printf '%s' "${show_body}" \
                    | jq -r '[.events[]? | select(.eventType == "MessageReceived" and ((.summary // "") | test("from agent:")))][-1] | (.body // "")' \
                    2>/dev/null || true)"
            fi
            printf '%s' "${body}"
            return 0
        fi

        sleep 2
    done
    return 1
}

# --- Three turns ------------------------------------------------------------
# Each turn uses a deliberately short, deterministic prompt so a small
# model (llama3.2:3b) stays within turn_timeout.
turn=1
for prompt in \
    "Reply with just the single word 'hello' and nothing else." \
    "Reply with just the single word 'one' and nothing else." \
    "Reply with just the single word 'two' and nothing else."
do
    e2e::log "turn ${turn}: spring message send agent://${agent} '${prompt}'"
    response="$(e2e::cli --output json message send "agent://${agent}" "${prompt}" --thread "${thread_id}")"
    code="${response##*$'\n'}"
    body="${response%$'\n'*}"
    e2e::expect_status "0" "${code}" "turn ${turn} message send succeeds"
    e2e::expect_contains "messageId" "${body}" "turn ${turn} send response carries a messageId"

    if reply="$(wait_for_agent_reply "${turn}")"; then
        if [[ -n "${reply}" ]]; then
            e2e::ok "turn ${turn} agent reply received (${#reply} chars)"
            e2e::log "turn ${turn} reply: ${reply:0:200}"
        else
            e2e::fail "turn ${turn} agent reply event found but body was empty"
        fi
    else
        e2e::fail "turn ${turn} agent did not reply within ${turn_timeout}s"
        break
    fi

    turn=$((turn+1))
done

e2e::summary
