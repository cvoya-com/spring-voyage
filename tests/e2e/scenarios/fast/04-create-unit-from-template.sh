#!/usr/bin/env bash
# Create a unit from the engineering-team template — exercises the skill-
# bundle resolver + validator + connector binding preview path.
#
# #325 added an optional UnitName override to CreateUnitFromTemplateRequest,
# and #316 exposed the endpoint through the CLI. We pass a run-scoped id as
# --name so two concurrent runs no longer collide on the template manifest's
# fixed `name` ("engineering-team"). That makes this scenario safe to run in
# parallel — restore a @serial note here only if UnitName is ever removed.
#
# Agent-presence assertions (#340): previous revisions only asserted the
# response shape (200 + `membersAdded: 3`). Issue #340 demonstrated that
# `membersAdded` could report success while the DB membership table stayed
# empty, so the Agents tab / `/memberships` / `/agents` endpoints all
# returned []. We now cross-verify all three read paths — CLI
# `unit members list`, HTTP `/memberships`, HTTP `/agents` — must agree
# on count AND expose the three template members (tech-lead,
# backend-engineer, qa-engineer). If any path disagrees with the others
# post-#340-fix, that is a new regression.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

# Run-scoped unit name so repeated / concurrent invocations don't clash on
# the template's fixed manifest name. The cascading purge trap uses this
# value, so the teardown path matches whatever we created here.
template_unit="$(e2e::unit_name from-template)"
trap 'e2e::cleanup_unit "${template_unit}"' EXIT

e2e::log "GET /api/v1/tenant/packages/templates (discover templates)"
response="$(e2e::http GET /api/v1/tenant/packages/templates)"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status 200 "${status}" "templates endpoint returns 200"
e2e::expect_contains 'engineering-team' "${body}" "engineering-team template is listed"

# Exercise the CLI path (#316). The command maps to
# POST /api/v1/units/from-template with UnitName=${template_unit} (#325).
e2e::log "spring unit create --from-template software-engineering/engineering-team --name ${template_unit}"
response="$(e2e::cli_unit_create --output json --from-template software-engineering/engineering-team --name "${template_unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "from-template CLI create succeeds"
e2e::expect_contains "\"name\": \"${template_unit}\"" "${body}" "created unit carries the run-scoped UnitName override (#325)"

# --- Verify membership is visible across all three read paths (#340) ---------
#
# The endpoints below resolve the unit by its address-path name (same token
# the CLI command used via --name). No id extraction needed because the
# server routes both `{id}` and the CLI-issued name through DirectoryService.

# 1) CLI: `spring unit members list` exercises the Kiota-generated client +
# the CLI's argument/output parsing path. The engineering-team template
# declares three members; we assert all three show up exactly.
e2e::log "spring unit members list ${template_unit} --output json"
response="$(e2e::cli --output json unit members list "${template_unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit members list succeeds for template-created unit"
# #1060: the JSON `member` field carries the scheme-prefixed canonical
# address (`agent://<path>` for agent rows, `unit://<path>` for sub-unit
# rows) so callers don't have to coalesce `agentAddress` with `subUnitId`
# per row. The CLI's --output json uses indented spacing (space after the
# colon); the compact-spacing variant lives on the HTTP /memberships
# surface tested in fast/06 (#1090).
e2e::expect_contains "\"member\": \"agent://tech-lead\"" "${body}" "members list includes tech-lead"
e2e::expect_contains "\"member\": \"agent://backend-engineer\"" "${body}" "members list includes backend-engineer"
e2e::expect_contains "\"member\": \"agent://qa-engineer\"" "${body}" "members list includes qa-engineer"
cli_count="$(printf '%s' "${body}" | grep -o '"member"' | wc -l | tr -d '[:space:]')"
if [[ "${cli_count}" == "3" ]]; then
    e2e::ok "members list returns exactly 3 members (got ${cli_count})"
else
    e2e::fail "members list count mismatch — expected 3, got ${cli_count}"
fi

# 2) HTTP: /api/v1/units/{id}/memberships — the Agents tab's read path.
# Must agree with the CLI list above; if not, there's drift between the
# CLI route and the DB read (the exact bug class #340 captured).
e2e::log "GET /api/v1/units/${template_unit}/memberships"
response="$(e2e::http GET "/api/v1/tenant/units/${template_unit}/memberships")"
status="${response##*$'\n'}"
mships_body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "/memberships returns 200 for template unit"
e2e::expect_contains "tech-lead" "${mships_body}" "/memberships includes tech-lead"
e2e::expect_contains "backend-engineer" "${mships_body}" "/memberships includes backend-engineer"
e2e::expect_contains "qa-engineer" "${mships_body}" "/memberships includes qa-engineer"
mships_count="$(printf '%s' "${mships_body}" | grep -o '"agentAddress"' | wc -l | tr -d '[:space:]')"
if [[ "${mships_count}" == "3" ]]; then
    e2e::ok "/memberships returns exactly 3 rows (got ${mships_count})"
else
    e2e::fail "/memberships count mismatch — expected 3, got ${mships_count}"
fi

# 3) HTTP: /api/v1/units/{id}/agents — the UI's Agents tab data source.
# Reads the unit actor's member list and enriches with agent metadata;
# must agree with /memberships on count. #340 had this path returning []
# while the CLI-less /members actor view was correctly populated.
e2e::log "GET /api/v1/units/${template_unit}/agents"
response="$(e2e::http GET "/api/v1/tenant/units/${template_unit}/agents")"
status="${response##*$'\n'}"
agents_body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "/agents returns 200 for template unit"
e2e::expect_contains "tech-lead" "${agents_body}" "/agents includes tech-lead"
e2e::expect_contains "backend-engineer" "${agents_body}" "/agents includes backend-engineer"
e2e::expect_contains "qa-engineer" "${agents_body}" "/agents includes qa-engineer"

# Cross-verification: the two HTTP endpoints and the CLI must agree on
# count. Any disagreement is a regression of the #340 class and should
# fail loudly here rather than bubble up as a silent UI emptiness bug.
if [[ "${cli_count}" == "${mships_count}" ]]; then
    e2e::ok "CLI members list and /memberships agree on count (${cli_count})"
else
    e2e::fail "CLI/memberships count drift — CLI=${cli_count}, /memberships=${mships_count}"
fi

# --- #374: verify agents are auto-registered as directory entries ----------
#
# Template-created agents must be discoverable via the platform-wide agents
# list (GET /api/v1/agents) and the CLI's `spring agent list`. Without the
# #374 fix, the directory has no entries for these agents and both paths
# return [].

e2e::log "spring agent list --output json"
response="$(e2e::cli --output json agent list)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "agent list succeeds after template creation"
e2e::expect_contains "tech-lead" "${body}" "agent list includes tech-lead (#374)"
e2e::expect_contains "backend-engineer" "${body}" "agent list includes backend-engineer (#374)"
e2e::expect_contains "qa-engineer" "${body}" "agent list includes qa-engineer (#374)"
agent_count="$(printf '%s' "${body}" | grep -o '"name"' | wc -l | tr -d '[:space:]')"
if [[ "${agent_count}" -ge "3" ]]; then
    e2e::ok "agent list returns at least 3 agents (got ${agent_count})"
else
    e2e::fail "agent list count — expected ≥3, got ${agent_count}"
fi

e2e::summary
