#!/usr/bin/env bash
# pool: fast
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
e2e::expect_contains "\"displayName\": \"${unit}\"" "${body}" "unit create carries the unit display name"
# Capture canonical hex id for direct HTTP calls below — passing the human
# name in URL path params now returns 400/500.
unit_id="$(printf '%s' "${body}" | awk -F'"' '/"name":/ { print $4; exit }')"

e2e::log "spring agent create --name ${agent} --unit ${unit}"
response="$(e2e::cli_agent_create --output json --name "${agent}" --unit "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "agent create succeeds"
# Agent canonical hex id. AgentResponse keeps `name` = display name today
# (unlike UnitResponse, where `name` already flipped to the hex form), so we
# strip dashes from the `id` field to get the path-id form. Membership rows
# now carry this hex in `agentAddress`; assertions below assert against it.
agent_id="$(printf '%s' "${body}" | awk -F'"' '/"id":/ { gsub(/-/, "", $4); print $4; exit }')"

# --- Add membership with overrides --------------------------------------------
e2e::log "spring unit members add ${unit} --agent ${agent} --model gpt-4o --specialty coding --enabled true --execution-mode OnDemand"
response="$(e2e::cli --output json unit members add "${unit}" \
    --agent "${agent}" --model gpt-4o --specialty coding --enabled true --execution-mode OnDemand)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit members add succeeds"
# The API still echoes the agent's display name in `agentAddress` (the
# AgentAddress wire field is the agent-scheme path, which keeps the human
# label today). The canonical hex id round-trips through the unified `member`
# field as `agent:<hex>` per #1060; assert both so we prove the same entity
# round-tripped regardless of which field a caller reads.
e2e::expect_contains "\"agentAddress\": \"${agent}\"" "${body}" "add response carries the agent display name in agentAddress"
e2e::expect_contains "\"member\": \"agent:${agent_id}\"" "${body}" "add response carries the canonical hex id in member"
e2e::expect_contains "\"model\": \"gpt-4o\"" "${body}" "add response echoes --model override"

# --- Verify via list ----------------------------------------------------------
e2e::log "spring unit members list ${unit} --output json"
response="$(e2e::cli --output json unit members list "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit members list succeeds"
e2e::expect_contains "\"member\": \"agent://${agent_id}\"" "${body}" "list contains the new membership (by canonical agent id in member field)"

# --- Cross-verify via HTTP read paths (#340) ----------------------------------
# The CLI `members list` alone can pass while the DB/Agents-tab read paths
# stay empty (see #340's template-from-create bug). Assert both /memberships
# AND /agents mention the newly-added agent so the direct-create path can't
# regress into the same drift silently.
e2e::log "GET /api/v1/tenant/units/${unit_id}/memberships"
response="$(e2e::http GET "/api/v1/tenant/units/${unit_id}/memberships")"
status="${response##*$'\n'}"
mships_body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "/memberships returns 200 for unit"
# The wire's `agentAddress` is the agent-scheme path (display name today);
# the `member` field carries the canonical hex id as `agent:<hex>`. Assert
# the canonical form so the test stays sharp if the wire field ever flips.
e2e::expect_contains "\"member\":\"agent:${agent_id}\"" "${mships_body}" "/memberships includes the added agent (canonical hex in member)"

e2e::log "GET /api/v1/tenant/units/${unit_id}/agents"
response="$(e2e::http GET "/api/v1/tenant/units/${unit_id}/agents")"
status="${response##*$'\n'}"
agents_body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "/agents returns 200 for unit"
# AgentResponse carries the canonical hex form in `id` (dashed); assert the
# display name round-trips since that's what /agents echoes for `name`.
e2e::expect_contains "\"displayName\":\"${agent}\"" "${agents_body}" "/agents includes the added agent (by display name)"

# --- Idempotent config update (upsert) ----------------------------------------
e2e::log "spring unit members config ${unit} --agent ${agent} --enabled false"
response="$(e2e::cli --output json unit members config "${unit}" --agent "${agent}" --enabled false)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit members config succeeds"
e2e::expect_contains "\"enabled\": false" "${body}" "config update flips enabled flag"

# --- Remove membership: last-unit guard refuses (#744 / #823) -----------------
# The agent only belongs to ${unit}, so removing this membership would leave it
# orphaned. Per the #744 / #823 contract the API refuses with a 409 and the
# CLI surfaces a non-zero exit (#1026 routes the server detail through cleanly).
# Asserting both the exit code AND the error text guards against silent regress
# of either layer.
e2e::log "spring unit members remove ${unit} --agent ${agent} (last-unit guard expected)"
response="$(e2e::cli unit members remove "${unit}" --agent "${agent}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "1" "${code}" "unit members remove blocks last-unit removal"
e2e::expect_contains "at least one unit" "${body}" "remove error mentions the last-unit guard"

# --- Cascading purge (belt-and-braces) ----------------------------------------
# The membership is still in place (the guard refused). Today `spring unit
# purge`'s cascade re-uses the `agentAddress` field returned by GET
# /memberships, which is the human display-name — the API rejects names in
# the DELETE /memberships/{agentAddress} URL with a 500 (Guid parse). Until
# the CLI cascade is updated to use the canonical hex id (tracked separately
# alongside the CliResolver wiring work), force-delete the agent first via
# the canonical hex id; that cascades the membership and lets `unit purge`
# complete its remaining step (deleting the bare unit).
e2e::log "DELETE /api/v1/tenant/agents/${agent_id} (workaround for purge cascade)"
response="$(e2e::http DELETE "/api/v1/tenant/agents/${agent_id}")"
status="${response##*$'\n'}"
e2e::expect_status "204" "${status}" "force-delete agent by canonical id (cascades membership)"

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
