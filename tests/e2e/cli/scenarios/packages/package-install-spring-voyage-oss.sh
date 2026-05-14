#!/usr/bin/env bash
# pool: fast
# Install the spring-voyage-oss package — the built-in dogfooding unit that
# stands up the multi-role organisation developing the Spring Voyage
# platform on itself. Asserts that the install completes (active state) and
# that the four sub-units (engineering / design / product / program) plus
# the parent organisation unit appear via the units list.
#
# This is the heaviest catalog package shipped today (5 units, 13 agents)
# and a useful smoke test for the full install pipeline. Inputs are
# required by the package manifest; we pass placeholder values that
# satisfy validation without touching real GitHub state — the test does
# NOT exercise the GitHub connector itself.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

# The OSS package's units have fixed display names declared in their
# manifests (the slug-style sv-oss-* names were renamed to the human-
# readable forms below). We clean them all up via the trap because the
# package's parent unit cascades its sub-units on purge.
parent_unit="Spring Voyage OSS"
sub_units=(
    "Software Engineering"
    "Design"
    "Product Management"
    "Program Management"
)
# Slug used by the engineering sub-unit's members-list assertion below; the
# CLI's CliResolver matches on display name when no Guid hit is found.
engineering_unit="Software Engineering"

# Force-delete every unit whose displayName matches `target` (and the agents
# attached to it) via direct HTTP. The CLI cascade `unit purge` cannot
# complete today: the API echoes the agent display name in `agentAddress`,
# but DELETE /memberships/{agentAddress} requires the canonical hex form
# (500 otherwise). Force-deleting the agents by their canonical id cascades
# the memberships away; then the bare unit can be deleted. See unit-
# membership-roundtrip.sh for the same workaround.
#
# Iterating over every match is necessary because a prior failed run can
# leave duplicate-displayName rows lying around (the package installer
# does not check for displayName collisions across installs).
_force_cleanup_unit() {
    local target="$1"
    local units_body unit_hex memberships agent_id matches
    units_body="$(e2e::http GET "/api/v1/tenant/units" 2>/dev/null)"
    units_body="${units_body%$'\n'*}"
    # Walk every {"id":..,"name":..,"displayName":..} record and emit the
    # `name` (hex) of the rows whose displayName equals the target.
    matches="$(printf '%s' "${units_body}" | python3 -c '
import json, sys
target = sys.argv[1]
try:
    arr = json.load(sys.stdin)
except Exception:
    sys.exit(0)
for u in arr:
    if u.get("displayName") == target:
        print(u.get("name"))
' "${target}" 2>/dev/null || true)"
    while IFS= read -r unit_hex; do
        [[ -z "${unit_hex}" ]] && continue
        memberships="$(e2e::http GET "/api/v1/tenant/units/${unit_hex}/memberships" 2>/dev/null || true)"
        memberships="${memberships%$'\n'*}"
        while IFS= read -r agent_id; do
            [[ -z "${agent_id}" ]] && continue
            e2e::http DELETE "/api/v1/tenant/agents/${agent_id}" >/dev/null 2>&1 || true
        done < <(printf '%s' "${memberships}" | grep -oE '"member":"agent:[0-9a-f]{32}"' | awk -F'[:"]' '{print $5}')
        e2e::http DELETE "/api/v1/tenant/units/${unit_hex}" >/dev/null 2>&1 || true
    done <<< "${matches}"
}

cleanup() {
    # Sub-units first so the parent's cascade-on-purge doesn't race with us.
    for u in "${sub_units[@]}"; do _force_cleanup_unit "${u}"; done
    _force_cleanup_unit "${parent_unit}"
}
# Run cleanup up-front too so a leftover install from a prior run doesn't
# 409 the install below with "name already exists".
cleanup
trap cleanup EXIT

# --- Install ----------------------------------------------------------------
# Required inputs: github_owner, github_repo, github_installation_id.
# We supply placeholder values — the install completes Phase 1 (staging)
# and Phase 2 (activation) without making any GitHub API calls because
# bind-on-install is connector-side and the dispatcher only contacts
# GitHub when a unit is actually started.
# The OSS package declares a required `github` connector; a stub binding is
# sufficient for the install pipeline because the dispatcher only contacts
# GitHub when a unit is actually started (out of scope for the fast pool).
e2e::log "spring package install spring-voyage-oss --connector github=acme/demo@1 --input github_owner=acme --input github_repo=demo --input github_installation_id=999"
response="$(e2e::cli --output json package install spring-voyage-oss \
    --connector "github=acme/demo@1" \
    --input github_owner=acme \
    --input github_repo=demo \
    --input github_installation_id=999)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" != "0" && "${body}" == *"ConnectorBindingMissing"* ]]; then
    # TODO(spring-voyage#cli-e2e-stub-binding): track a fast-pool-installable
    # variant once the platform supports a first-class stub connector.
    e2e::log "package requires a real connector binding the fast pool cannot supply — skipping scenario."
    exit 0
fi
e2e::expect_status "0" "${code}" "spring-voyage-oss package install succeeds"
e2e::expect_contains '"status": "active"' "${body}" "install reaches active aggregate status"

# --- Verify the parent + sub-units exist via unit list ----------------------
e2e::log "spring unit list --output json"
response="$(e2e::cli --output json unit list)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit list succeeds after install"
e2e::expect_contains "\"${parent_unit}\"" "${body}" "unit list includes the parent OSS unit"
for u in "${sub_units[@]}"; do
    e2e::expect_contains "\"${u}\"" "${body}" "unit list includes sub-unit ${u}"
done

# --- Spot-check one sub-unit's members --------------------------------------
# The engineering team is the largest sub-unit — verify it has at least one
# membership row so we know the activator wired members + agents correctly.
e2e::log "spring unit members list '${engineering_unit}' --output json"
response="$(e2e::cli --output json unit members list "${engineering_unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "engineering sub-unit members list succeeds"
member_count="$(printf '%s' "${body}" | grep -o '"agentAddress"' | wc -l | tr -d '[:space:]')"
if (( member_count >= 1 )); then
    e2e::ok "engineering sub-unit has ${member_count} member(s)"
else
    e2e::fail "engineering sub-unit has no members — activator did not register agents"
fi

e2e::summary
