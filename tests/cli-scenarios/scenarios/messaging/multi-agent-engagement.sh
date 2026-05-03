#!/usr/bin/env bash
# pool: llm
# Multiple-agent engagement observation. Creates a unit with two dapr-agent
# members, dispatches a prompt to each (on separate threads), and verifies
# both engagements are observable via the engagement list / thread show
# CLI surfaces.
#
# True autonomous agent-to-agent forwarding requires prompt engineering
# the model cannot reliably honour at this scale; this scenario instead
# exercises the operator-driven multi-participant flow and the observability
# surface (engagement list / thread show) that an operator would use to
# watch agents work in parallel.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

if ! e2e::require_ollama; then
    e2e::log "skipping: Ollama not reachable"
    exit 0
fi

unit="$(e2e::unit_name multi-agent)"
agent_a="$(e2e::agent_name multi-agent-a)"
agent_b="$(e2e::agent_name multi-agent-b)"
image="${SPRING_DAPR_AGENT_IMAGE:-localhost/spring-voyage-agent-dapr:latest}"
model="${SPRING_DAPR_AGENT_MODEL:-llama3.2:3b}"
provider="${SPRING_DAPR_AGENT_PROVIDER:-ollama}"
# Each agent is dispatched on its own thread so the actors don't share a
# single per-thread queue. Two engagements end up observable via
# `engagement list --unit ${unit}`.
thread_a="e2e-multi-agent-a-$(date +%s)-$$"
thread_b="e2e-multi-agent-b-$(date +%s)-$$"
turn_timeout="${SPRING_MULTI_AGENT_TURN_TIMEOUT:-300}"

cleanup() {
    e2e::cli unit purge "${unit}" --confirm >/dev/null 2>&1 || true
    e2e::cli agent purge "${agent_a}" --confirm >/dev/null 2>&1 || true
    e2e::cli agent purge "${agent_b}" --confirm >/dev/null 2>&1 || true
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

for agent in "${agent_a}" "${agent_b}"; do
    e2e::log "spring agent create ${agent} --unit ${unit}"
    response="$(e2e::cli_agent_create --output json "${agent}" \
        --unit "${unit}" \
        --name "Multi-agent test agent" \
        --definition "${definition}")"
    code="${response##*$'\n'}"
    e2e::expect_status "0" "${code}" "agent create ${agent} succeeds"
done

# Helper: poll thread show until at least 1 agent-from event lands.
wait_for_agent_reply_on_thread() {
    local thread="$1"
    local deadline=$(( $(date +%s) + turn_timeout ))
    while (( $(date +%s) < deadline )); do
        local show_raw show_code show_body
        show_raw="$(e2e::cli --output json thread show "${thread}" 2>&1)" || true
        show_code="${show_raw##*$'\n'}"
        show_body="${show_raw%$'\n'*}"
        if [[ "${show_code}" == "0" ]] && printf '%s' "${show_body}" | grep -q 'from agent:'; then
            return 0
        fi
        sleep 2
    done
    return 1
}

# Send to agent_a, wait for reply, then send to agent_b. Running them
# sequentially keeps a single-Ollama-instance host from serialising both
# turns into one timeout window.
e2e::log "spring message send agent://${agent_a} (thread=${thread_a})"
response="$(e2e::cli --output json message send "agent://${agent_a}" \
    "Reply with just the single word 'a' and nothing else." \
    --thread "${thread_a}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "send to agent_a succeeds"

if wait_for_agent_reply_on_thread "${thread_a}"; then
    e2e::ok "agent_a thread surfaces a reply"
else
    e2e::fail "agent_a thread did not surface a reply within ${turn_timeout}s"
fi

e2e::log "spring message send agent://${agent_b} (thread=${thread_b})"
response="$(e2e::cli --output json message send "agent://${agent_b}" \
    "Reply with just the single word 'b' and nothing else." \
    --thread "${thread_b}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "send to agent_b succeeds"

if wait_for_agent_reply_on_thread "${thread_b}"; then
    e2e::ok "agent_b thread surfaces a reply"
else
    e2e::fail "agent_b thread did not surface a reply within ${turn_timeout}s"
fi

# Engagement list scoped to each agent should now include the matching thread.
e2e::log "spring engagement list --agent ${agent_a} --output json"
response="$(e2e::cli --output json engagement list --agent "${agent_a}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "engagement list --agent agent_a succeeds"
e2e::expect_contains "${thread_a}" "${body}" "agent_a engagement list mentions thread_a"

e2e::log "spring engagement list --agent ${agent_b} --output json"
response="$(e2e::cli --output json engagement list --agent "${agent_b}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "engagement list --agent agent_b succeeds"
e2e::expect_contains "${thread_b}" "${body}" "agent_b engagement list mentions thread_b"

e2e::summary
