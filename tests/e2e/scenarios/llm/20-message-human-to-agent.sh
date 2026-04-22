#!/usr/bin/env bash
# LLM scenario: human-to-agent messaging round-trip.
#
# Creates a unit + agent, adds the agent to the unit, sends a human-authored
# message to the agent through the CLI, and asserts the agent response came
# back. Requires a reachable Ollama server (scenario skips cleanly otherwise).
#
# The assertion is deliberately shallow — agent responses are non-deterministic,
# and the purpose of this scenario is to prove the platform wiring (message
# routing, agent turn dispatch, LLM call) works end-to-end. We only require that
# the send call succeeds and a message id is returned.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

if ! e2e::require_ollama; then
    e2e::log "skipping: Ollama not reachable"
    exit 0
fi

unit="$(e2e::unit_name llm-human-agent)"
agent="$(e2e::agent_name llm-human-agent)"

cleanup() {
    e2e::cli unit purge "${unit}" --confirm >/dev/null 2>&1 || true
    e2e::cli agent purge "${agent}" --confirm >/dev/null 2>&1 || true
}
trap cleanup EXIT

# --- Setup -------------------------------------------------------------------
e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit create succeeds"

e2e::log "spring agent create ${agent}"
response="$(e2e::cli --output json agent create "${agent}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "agent create succeeds"

e2e::log "spring unit members add ${unit} --agent ${agent}"
response="$(e2e::cli --output json unit members add "${unit}" --agent "${agent}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "membership add succeeds"

# --- Send a human message to the agent ---------------------------------------
conv_id="e2e-conv-$(date +%s)"
e2e::log "spring message send agent://${agent} '...'"
response="$(e2e::cli --output json message send "agent://${agent}" \
    "Respond with exactly one word: hello" \
    --conversation "${conv_id}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "message send succeeds"
e2e::expect_contains "messageId" "${body}" "send response carries a messageId"

e2e::summary
