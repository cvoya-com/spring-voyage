#!/usr/bin/env bash
# Nested units: create parent + child via CLI, add the child as a member of
# the parent, verify the parent's member list exposes the child. Cascading
# purge on EXIT cleans both up, even if the scenario aborts mid-way.
#
# #331 landed: `spring unit members add <parent> --unit <child>` targets the
# scheme-agnostic POST /api/v1/units/{id}/members endpoint, so the step that
# previously fell back to `e2e::http` is now fully CLI-driven.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

parent="$(e2e::unit_name parent)"
child="$(e2e::unit_name child)"

# Cascading teardown covers both units even if any assertion aborts the script.
# Purge is idempotent on the server side, so re-running after a partial failure
# is safe.
trap 'e2e::cleanup_unit "${parent}" "${child}"' EXIT

# --- Setup: create parent and child via CLI -----------------------------------
e2e::log "spring unit create ${parent} --top-level"
response="$(e2e::cli_unit_create --output json "${parent}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "parent unit create succeeds"
e2e::expect_contains "\"name\": \"${parent}\"" "${body}" "parent create response carries the unit name"

# Child binds to the parent via --parent-unit — this exercises the
# non-top-level code path added by #744 alongside the parent's top-level
# path above.
e2e::log "spring unit create ${child} --parent-unit ${parent}"
response="$(e2e::cli_unit_create --output json "${child}" --parent-unit "${parent}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "child unit create succeeds"
e2e::expect_contains "\"name\": \"${child}\"" "${body}" "child create response carries the unit name"

# --- Add child as member of parent (CLI, #331) --------------------------------
# `spring unit members add <parent> --unit <child>` POSTs to the scheme-
# agnostic /api/v1/units/{id}/members endpoint with memberAddress={
# scheme: "unit", path: <child> }. Exit code 0 on success; the CLI prints a
# confirmation line we don't inspect here.
e2e::log "spring unit members add ${parent} --unit ${child}"
response="$(e2e::cli unit members add "${parent}" --unit "${child}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "add child as member of parent via CLI succeeds"

# --- Verify via GET /api/v1/units/{id} ----------------------------------------
# GetUnitAsync returns UnitDetailResponse { unit, details } where `details` is
# the actor's StatusQuery payload. That payload includes the current member
# list; we assert the child's address appears in the raw JSON rather than
# parsing the full shape (which would couple us to the actor's reply schema).
e2e::log "GET /api/v1/units/${parent}"
response="$(e2e::http GET "/api/v1/tenant/units/${parent}")"
status="${response##*$'\n'}"
resp_body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "get parent unit returns 200"
e2e::expect_contains "${child}" "${resp_body}" "parent detail response mentions the child address"
e2e::expect_contains "\"unit\"" "${resp_body}" "parent detail response carries the unit scheme marker"

# --- Verify via `spring unit members list <parent> --output json` (#352) ------
# Post-#352 the CLI's members-list command joins the `unit_memberships` table
# (agent-scheme rows) with the actor's status-query payload (every scheme) so
# sub-units are no longer invisible on the CLI. Each row in the JSON output
# carries an explicit `scheme` field so callers can filter with jq.
e2e::log "spring unit members list ${parent} --output json"
response="$(e2e::cli --output json unit members list "${parent}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "members list succeeds"
e2e::expect_contains "\"scheme\": \"unit\"" "${body}" "members list emits scheme=unit for the sub-unit row"
# #1060: the unified `member` field is the scheme-prefixed canonical
# address (`unit://<child>` for the sub-unit row here), so scripts can
# read the member id without branching on agentAddress vs subUnitId.
e2e::expect_contains "\"member\": \"unit://${child}\"" "${body}" "members list emits the child's scheme-prefixed address on the unit row"

e2e::summary
