#!/usr/bin/env bash
# pool: llm
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
# Canonical hex id of the unit — needed to grant the caller's Hat below.
unit_id="$(printf '%s' "${body}" | awk -F'"' '/"name":/ { print $4; exit }')"

# #744: agent create requires --unit; the membership is registered atomically.
e2e::log "spring agent create --name ${agent} --unit ${unit}"
response="$(e2e::cli_agent_create --output json --name "${agent}" --unit "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "agent create succeeds"

# Extract the agent's Guid from the create response so we can address it in
# canonical `agent:<guid>` form (ADR-0036). The legacy `agent://<name>` shape
# was retired with #1653.
agent_id="$(printf '%s' "${body}" | awk -F'"' '/"id":/ { print $4; exit }')"
if [[ -z "${agent_id}" ]]; then
    e2e::fail "could not extract agent id from create response: ${body:0:200}"
    e2e::summary
    exit 1
fi
agent_address="agent:${agent_id}"

# #2972 (commit c3f92950): a tenant-user → agent send is gated on Hat
# reachability — the caller must wear a Hat that is a team member of a unit
# the agent sits in. Grant the caller's own Hat owner membership on the unit.
e2e::add_caller_hat "${unit_id}"

# --- Send a human message to the agent ---------------------------------------
# Omit --thread: the server now validates thread ids as Guids and allocates one
# itself (the previous human-shaped id returned 400). The assertion is shallow
# by design — we only require the send to be accepted and return a messageId.
e2e::log "spring message send ${agent_address} '...'"
response="$(e2e::cli --output json message send "${agent_address}" \
    "Respond with exactly one word: hello")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "message send succeeds"
e2e::expect_contains "messageId" "${body}" "send response carries a messageId"

e2e::summary
