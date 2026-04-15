#!/usr/bin/env bash
# Exercise the CLI surface added in #320: agent/unit membership management and
# the cascading `unit purge` helper. Starts from scratch (create unit, create
# agent), adds a membership with per-row overrides, verifies the list endpoint
# sees it, removes the membership, and then purges the unit to prove the
# cascading teardown works (belt-and-braces even though the membership is
# already gone).
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../_lib.sh"

suffix="$(date +%s%N | tail -c 6)"
unit="e2e-mship-unit-${suffix}"
agent="e2e-mship-agent-${suffix}"

cleanup() {
    # Best-effort: swallow any errors so a teardown failure doesn't mask the
    # real scenario outcome. Both purges are idempotent on the server side.
    e2e::cli unit purge "${unit}" --confirm >/dev/null 2>&1 || true
    e2e::cli agent purge "${agent}" --confirm >/dev/null 2>&1 || true
}
trap cleanup EXIT

# --- Setup: create unit and agent ---------------------------------------------
e2e::log "spring unit create ${unit}"
response="$(e2e::cli --output json unit create "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit create succeeds"
e2e::expect_contains "\"name\": \"${unit}\"" "${body}" "unit create carries the unit name"

e2e::log "spring agent create ${agent}"
response="$(e2e::cli --output json agent create "${agent}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "agent create succeeds"

# --- Add membership with overrides --------------------------------------------
e2e::log "spring unit members add ${unit} --agent ${agent} --model gpt-4o --specialty coding --enabled true --execution-mode OnDemand"
response="$(e2e::cli --output json unit members add "${unit}" \
    --agent "${agent}" --model gpt-4o --specialty coding --enabled true --execution-mode OnDemand)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit members add succeeds"
e2e::expect_contains "\"agentAddress\": \"${agent}\"" "${body}" "add response carries the agent address"
e2e::expect_contains "\"model\": \"gpt-4o\"" "${body}" "add response echoes --model override"

# --- Verify via list ----------------------------------------------------------
e2e::log "spring unit members list ${unit} --output json"
response="$(e2e::cli --output json unit members list "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit members list succeeds"
e2e::expect_contains "\"agentAddress\": \"${agent}\"" "${body}" "list contains the new membership"

# --- Idempotent config update (upsert) ----------------------------------------
e2e::log "spring unit members config ${unit} --agent ${agent} --enabled false"
response="$(e2e::cli --output json unit members config "${unit}" --agent "${agent}" --enabled false)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit members config succeeds"
e2e::expect_contains "\"enabled\": false" "${body}" "config update flips enabled flag"

# --- Remove membership --------------------------------------------------------
e2e::log "spring unit members remove ${unit} --agent ${agent}"
response="$(e2e::cli unit members remove "${unit}" --agent "${agent}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit members remove succeeds"

# --- Cascading purge (belt-and-braces) ----------------------------------------
# Re-add membership so purge actually has something to cascade through.
e2e::cli unit members add "${unit}" --agent "${agent}" >/dev/null

e2e::log "spring unit purge ${unit} --confirm"
response="$(e2e::cli unit purge "${unit}" --confirm)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit purge --confirm succeeds"
e2e::expect_contains "purged" "${body}" "purge output mentions success"

# --- Guardrail: purge without --confirm must refuse ---------------------------
# Create a second throw-away unit so the refusal path has something to protect.
guard_unit="e2e-mship-guard-${suffix}"
e2e::cli unit create "${guard_unit}" >/dev/null
e2e::log "spring unit purge ${guard_unit} (without --confirm — must refuse)"
response="$(e2e::cli unit purge "${guard_unit}")"
code="${response##*$'\n'}"
if [[ "${code}" != "0" ]]; then
    e2e::ok "purge without --confirm exits non-zero (exit ${code})"
else
    e2e::fail "purge without --confirm — expected non-zero exit, got ${code}"
fi
e2e::cli unit delete "${guard_unit}" >/dev/null || true

# --- Cascading agent purge ----------------------------------------------------
e2e::log "spring agent purge ${agent} --confirm"
response="$(e2e::cli agent purge "${agent}" --confirm)"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "agent purge --confirm succeeds"

e2e::summary
