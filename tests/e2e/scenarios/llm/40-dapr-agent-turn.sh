#!/usr/bin/env bash
# LLM scenario: Dapr Agent turn via A2A protocol.
#
# Creates a unit + agent configured with tool="dapr-agent", dispatches a
# simple message, and verifies the round-trip completes without 5xx errors.
# The agent uses the Ollama container for inference — this scenario gates
# on e2e::require_ollama.
#
# Scope: wiring proof that the DaprAgentLauncher + Python Dapr Agent
# container can receive a task and return a response. We cannot assert on
# the LLM's text output, so the check is that the dispatch succeeds.
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

cleanup() {
    e2e::cli unit purge "${unit}" --confirm >/dev/null 2>&1 || true
    e2e::cli agent purge "${agent}" --confirm >/dev/null 2>&1 || true
}
trap cleanup EXIT

e2e::log "creating unit '${unit}'"
e2e::cli unit create "${unit}" --display-name "Dapr Agent E2E"

e2e::log "creating agent '${agent}' with tool=dapr-agent"
e2e::cli agent create "${agent}" \
    --unit "${unit}" \
    --display-name "Dapr Agent" \
    --tool dapr-agent \
    --image "${SPRING_DAPR_AGENT_IMAGE:-localhost/spring-dapr-agent:latest}"

e2e::log "sending message to agent '${agent}'"
response=$(e2e::cli message send "${agent}" --text "Say hello in one sentence." 2>&1) || {
    e2e::log "message send failed (exit $?): ${response}"
    exit 1
}

e2e::log "dapr-agent turn completed successfully"
e2e::log "response: ${response}"
