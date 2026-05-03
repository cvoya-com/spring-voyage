#!/usr/bin/env bash
# pool: fast
# Install the `software-engineering` catalog package and verify the
# engineering-team unit + its three agents (tech-lead, backend-engineer,
# qa-engineer) appear across all three read paths: CLI `unit members list`,
# HTTP `/memberships`, and HTTP `/agents`. Replaces the now-deleted
# `--from-template` path (#1583 / 213f39bc) with `spring package install`.
#
# Why all three paths: #340 demonstrated drift where one read path
# reported success while another returned []. We continue to assert
# count agreement across the three to catch that class of regression.
#
# Naming: the package's declared unit name is `engineering-team`. We
# uninstall it via cascading purge in the EXIT trap; the scenario is
# NOT parallel-safe across two concurrent `run.sh` invocations because
# the unit name is fixed by the package. Operators running parallel
# suites should split installs across distinct catalog packages.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

template_unit="engineering-team"
trap 'e2e::cleanup_unit "${template_unit}"' EXIT

e2e::log "GET /api/v1/tenant/packages (discover catalog)"
response="$(e2e::http GET /api/v1/tenant/packages)"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status 200 "${status}" "catalog endpoint returns 200"
e2e::expect_contains 'software-engineering' "${body}" "software-engineering package is listed"

# Catalog install of the software-engineering package — creates the
# engineering-team unit + tech-lead / backend-engineer / qa-engineer.
e2e::log "spring package install software-engineering"
response="$(e2e::cli --output json package install software-engineering)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "package install software-engineering succeeds"
e2e::expect_contains '"status"' "${body}" "install response carries a status field"

# --- Verify membership is visible across all three read paths (#340) ---------

# 1) CLI: `spring unit members list` — Kiota client + CLI output formatter.
e2e::log "spring unit members list ${template_unit} --output json"
response="$(e2e::cli --output json unit members list "${template_unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit members list succeeds for installed unit"
# Server emits the per-row agent slug in `agentAddress`. The `member` field
# carries the identity URI (`agent:id:<uuid>`) post-#1492, so we assert on
# `agentAddress` instead — same pattern as unit-membership-roundtrip.
e2e::expect_contains "\"agentAddress\": \"tech-lead\"" "${body}" "members list includes tech-lead"
e2e::expect_contains "\"agentAddress\": \"backend-engineer\"" "${body}" "members list includes backend-engineer"
e2e::expect_contains "\"agentAddress\": \"qa-engineer\"" "${body}" "members list includes qa-engineer"
cli_count="$(printf '%s' "${body}" | grep -o '"agentAddress"' | wc -l | tr -d '[:space:]')"
if [[ "${cli_count}" == "3" ]]; then
    e2e::ok "members list returns exactly 3 members (got ${cli_count})"
else
    e2e::fail "members list count mismatch — expected 3, got ${cli_count}"
fi

# 2) HTTP: /api/v1/units/{id}/memberships — the Agents tab read path.
e2e::log "GET /api/v1/units/${template_unit}/memberships"
response="$(e2e::http GET "/api/v1/tenant/units/${template_unit}/memberships")"
status="${response##*$'\n'}"
mships_body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "/memberships returns 200 for installed unit"
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
e2e::log "GET /api/v1/units/${template_unit}/agents"
response="$(e2e::http GET "/api/v1/tenant/units/${template_unit}/agents")"
status="${response##*$'\n'}"
agents_body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "/agents returns 200 for installed unit"
e2e::expect_contains "tech-lead" "${agents_body}" "/agents includes tech-lead"
e2e::expect_contains "backend-engineer" "${agents_body}" "/agents includes backend-engineer"
e2e::expect_contains "qa-engineer" "${agents_body}" "/agents includes qa-engineer"

# Cross-verification: paths must agree on count.
if [[ "${cli_count}" == "${mships_count}" ]]; then
    e2e::ok "CLI members list and /memberships agree on count (${cli_count})"
else
    e2e::fail "CLI/memberships count drift — CLI=${cli_count}, /memberships=${mships_count}"
fi

# --- #374: agents are auto-registered as directory entries ------------------
e2e::log "spring agent list --output json"
response="$(e2e::cli --output json agent list)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "agent list succeeds after package install"
e2e::expect_contains "tech-lead" "${body}" "agent list includes tech-lead (#374)"
e2e::expect_contains "backend-engineer" "${body}" "agent list includes backend-engineer (#374)"
e2e::expect_contains "qa-engineer" "${body}" "agent list includes qa-engineer (#374)"

e2e::summary
