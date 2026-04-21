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
source "${HERE}/../../_lib.sh"

# Align with the shared run-id naming so --sweep picks these up even if both
# the unit purge and agent purge somehow fail. Previous revision used a local
# suffix; the common helper avoids the double-vocabulary drift flagged during
# the phase-2 retrofit.
unit="$(e2e::unit_name mship-unit)"
agent="$(e2e::agent_name mship-agent)"
guard_unit="$(e2e::unit_name mship-guard)"

# One trap, three handles. cleanup_unit cascades through memberships, so the
# agent is only torn down after the explicit cleanup_agent call — matching
# the server-side order. Every purge is best-effort; a teardown failure can
# never mask the scenario's real exit code.
trap 'e2e::cleanup_unit "${unit}" "${guard_unit}"; e2e::cleanup_agent "${agent}"' EXIT

# --- Setup: create unit and agent ---------------------------------------------
e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit create succeeds"
e2e::expect_contains "\"name\": \"${unit}\"" "${body}" "unit create carries the unit name"

e2e::log "spring agent create ${agent} --unit ${unit}"
response="$(e2e::cli_agent_create --output json "${agent}" --unit "${unit}")"
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

# --- Cross-verify via HTTP read paths (#340) ----------------------------------
# The CLI `members list` alone can pass while the DB/Agents-tab read paths
# stay empty (see #340's template-from-create bug). Assert both /memberships
# AND /agents mention the newly-added agent so the direct-create path can't
# regress into the same drift silently.
e2e::log "GET /api/v1/units/${unit}/memberships"
response="$(e2e::http GET "/api/v1/units/${unit}/memberships")"
status="${response##*$'\n'}"
mships_body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "/memberships returns 200 for unit"
e2e::expect_contains "\"agentAddress\": \"${agent}\"" "${mships_body}" "/memberships includes the added agent"

e2e::log "GET /api/v1/units/${unit}/agents"
response="$(e2e::http GET "/api/v1/units/${unit}/agents")"
status="${response##*$'\n'}"
agents_body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "/agents returns 200 for unit"
e2e::expect_contains "${agent}" "${agents_body}" "/agents includes the added agent"

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
# The EXIT trap cascades it; the main ${unit} is already gone and that purge
# no-ops cleanly.
e2e::cli_unit_create "${guard_unit}" >/dev/null
e2e::log "spring unit purge ${guard_unit} (without --confirm — must refuse)"
response="$(e2e::cli unit purge "${guard_unit}")"
code="${response##*$'\n'}"
if [[ "${code}" != "0" ]]; then
    e2e::ok "purge without --confirm exits non-zero (exit ${code})"
else
    e2e::fail "purge without --confirm — expected non-zero exit, got ${code}"
fi

e2e::summary
