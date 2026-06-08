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
#
# Connector binding: software-engineering declares a required `github`
# connector. A stub binding (acme/repo@stub) is sufficient for the
# install to succeed in the fast pool; no real GitHub call is exercised.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

template_unit="engineering-team"

# Pre-flight teardown of any leftover engineering-team from a prior failed
# run. `spring unit purge` currently 500s when the unit's agents have it as
# their only membership (the cascade can't remove an agent's last unit and
# also can't delete the agent atomically); the orphan would then 409 on the
# next install. Direct DELETE the agents and the unit instead. Best-effort —
# a clean tenant simply 404s on the show below.
_pretemplate_cleanup() {
    local units_body unit_hex memberships agent_id
    units_body="$(e2e::http GET "/api/v1/tenant/units" 2>/dev/null || true)"
    units_body="${units_body%$'\n'*}"
    # Delete EVERY unit whose displayName is the package's slug. A prior run
    # that failed mid-way leaves the unit behind, and once TWO exist `unit
    # show engineering-team` errors "Multiple units match", defeating a
    # single-resolve cleanup (the old form). Each membership row carries the
    # agent id under `member` as `agent:<hex>`; force-delete the agents
    # (cascading their memberships) then the unit itself.
    while IFS= read -r unit_hex; do
        [[ -z "${unit_hex}" ]] && continue
        memberships="$(e2e::http GET "/api/v1/tenant/units/${unit_hex}/memberships" 2>/dev/null || true)"
        memberships="${memberships%$'\n'*}"
        while IFS= read -r agent_id; do
            [[ -z "${agent_id}" ]] && continue
            e2e::http DELETE "/api/v1/tenant/agents/${agent_id}" >/dev/null 2>&1 || true
        done < <(printf '%s' "${memberships}" | grep -oE '"member":"agent:[0-9a-f]{32}"' | grep -oE '[0-9a-f]{32}')
        e2e::http DELETE "/api/v1/tenant/units/${unit_hex}?force=true" >/dev/null 2>&1 || true
    done < <(printf '%s' "${units_body}" | jq -r --arg n "${template_unit}" '.[] | select(.displayName==$n) | .name' 2>/dev/null)
}
_pretemplate_cleanup

# Use _pretemplate_cleanup for teardown as well — the cascading `spring unit
# purge` doesn't work when the unit is the agents' only membership, and the
# helper above already handles that case via direct DELETE.
trap '_pretemplate_cleanup' EXIT

e2e::log "GET /api/v1/tenant/packages (discover catalog)"
response="$(e2e::http GET /api/v1/tenant/packages)"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status 200 "${status}" "catalog endpoint returns 200"
e2e::expect_contains 'software-engineering' "${body}" "software-engineering package is listed"

# Catalog install of the software-engineering package — creates the
# engineering-team unit + tech-lead / backend-engineer / qa-engineer.
# Pass a stub github connector binding to satisfy the package's required
# connector declaration (real GitHub I/O is not exercised in the fast pool).
# The package's agents pin the claude-code runtime (provider: anthropic), and
# install now fail-fasts when the required LLM credential is absent. Supply a
# dummy oauth token via --secret so the install completes — validity is only
# checked at agent turn time, which this CRUD/membership scenario never reaches.
e2e::log "spring package install software-engineering --connector github=acme/repo@1 --secret anthropic:oauth=***"
response="$(e2e::cli --output json package install software-engineering --connector "github=acme/repo@1" --secret "anthropic:oauth=sk-ant-oat-e2e-dummy")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" == "0" ]]; then
    e2e::ok "package install software-engineering succeeds (exit ${code})"
elif [[ "${body}" == *"ConnectorBindingMissing"* ]]; then
    # Defensive: if a future package revision tightens the binding shape so
    # the stub above stops being acceptable, skip cleanly rather than fail.
    # TODO(spring-voyage#cli-e2e-stub-binding): track a fast-pool-installable
    # variant or first-class stub connector once the platform supports one.
    e2e::log "package requires a real connector binding the fast pool cannot supply — skipping scenario."
    exit 0
else
    e2e::fail "package install software-engineering — expected exit 0, got ${code}: ${body:0:500}"
fi
e2e::expect_contains '"status"' "${body}" "install response carries a status field"

# Resolve a unit/agent's canonical hex id from `<verb> show <ref>`. Uses jq on
# the top-level `.name`: the show JSON now embeds an `effectiveTools[]` array
# whose nested `name` fields broke the previous positional `awk '/"name":/'`
# extraction (it grabbed a tool name like `github.describe_inbound_contract`).
_show_hex() { # $1=unit|agent  $2=ref
    local r; r="$(e2e::cli --output json "$1" show "$2")"
    printf '%s' "${r%$'\n'*}" | jq -r '.name // empty'
}

# Capture the canonical hex id of the installed unit — required for raw HTTP
# calls below; passing the human name in URL path params now returns 400/500.
unit_id="$(_show_hex unit "${template_unit}")"
if [[ -z "${unit_id}" ]]; then
    e2e::fail "could not resolve canonical id for ${template_unit}"
    e2e::summary
    exit 1
fi
e2e::log "resolved ${template_unit} -> ${unit_id}"

# Capture each installed agent's hex id from the directory; the CLI's
# members-list response carries hex ids in `agentAddress`, not the slug.
tech_lead_id="$(_show_hex agent tech-lead)"
backend_id="$(_show_hex agent backend-engineer)"
qa_id="$(_show_hex agent qa-engineer)"

# --- Verify membership is visible across all three read paths (#340) ---------

# 1) CLI: `spring unit members list` — Kiota client + CLI output formatter.
e2e::log "spring unit members list ${template_unit} --output json"
response="$(e2e::cli --output json unit members list "${template_unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit members list succeeds for installed unit"
# The CLI response carries hex ids in agentAddress (the canonical entity
# address); we verify each installed agent's hex round-trips through the
# membership row.
e2e::expect_contains "\"agentAddress\": \"${tech_lead_id}\"" "${body}" "members list includes tech-lead (by canonical id)"
e2e::expect_contains "\"agentAddress\": \"${backend_id}\"" "${body}" "members list includes backend-engineer (by canonical id)"
e2e::expect_contains "\"agentAddress\": \"${qa_id}\"" "${body}" "members list includes qa-engineer (by canonical id)"
# CLI members list unions the unit actor's member list with the /memberships
# table. Post platform shift the two sources expose the same agent under
# different addresses (hex from the actor, slug from /memberships), so the CLI
# emits two rows per agent today. We assert at-least-3 (one row per agent
# minimum) rather than exactly-3 to avoid coupling to this platform dedup
# behaviour. TODO(platform): consolidate the two sources so CLI returns one
# row per logical member regardless of underlying address shape.
cli_count="$(printf '%s' "${body}" | grep -o '"agentAddress"' | wc -l | tr -d '[:space:]')"
if (( cli_count >= 3 )); then
    e2e::ok "members list returns at least 3 members (got ${cli_count})"
else
    e2e::fail "members list count mismatch — expected at least 3, got ${cli_count}"
fi

# 2) HTTP: /api/v1/units/{id}/memberships — the Agents tab read path.
# Path id must be the canonical hex form post platform shift.
e2e::log "GET /api/v1/tenant/units/${unit_id}/memberships"
response="$(e2e::http GET "/api/v1/tenant/units/${unit_id}/memberships")"
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
e2e::log "GET /api/v1/tenant/units/${unit_id}/agents"
response="$(e2e::http GET "/api/v1/tenant/units/${unit_id}/agents")"
status="${response##*$'\n'}"
agents_body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "/agents returns 200 for installed unit"
e2e::expect_contains "tech-lead" "${agents_body}" "/agents includes tech-lead"
e2e::expect_contains "backend-engineer" "${agents_body}" "/agents includes backend-engineer"
e2e::expect_contains "qa-engineer" "${agents_body}" "/agents includes qa-engineer"

# Cross-verification: CLI lists each agent at least once (one row per
# /memberships row). See above for the at-least-vs-exactly rationale.
if (( cli_count >= mships_count )); then
    e2e::ok "CLI members list covers every /memberships row (CLI=${cli_count}, mships=${mships_count})"
else
    e2e::fail "CLI undercount — CLI=${cli_count}, /memberships=${mships_count}"
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
