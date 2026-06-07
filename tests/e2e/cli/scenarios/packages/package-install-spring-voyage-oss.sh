#!/usr/bin/env bash
# pool: fast
# Install the spring-voyage-oss package — the built-in dogfooding unit
# that stands up the OSS organisation developing the Spring Voyage
# platform on itself. Asserts that the install completes (active state)
# and that the single "Spring Voyage OSS" unit appears via the units list
# with its template-instantiated agent members wired. The package was
# flattened from sub-units to one unit + inline template agents (#2525),
# so this scenario no longer expects "Software Engineering" /
# "Program Management" sub-units.
#
# Inputs are required by the package manifest; we pass placeholder
# values that satisfy validation without touching real GitHub state —
# the test does NOT exercise the GitHub connector itself.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

# The OSS package ships a single unit whose displayName is the string
# below. The CLI's `unit list` table column collapses to
# displayName-when-present (falling back to the slug `name:`), so this is
# the string the operator sees on the API wire and at the table column.
# Keep it aligned with `displayName:` in
#   packages/spring-voyage-oss/units/spring-voyage-oss/package.yaml.
parent_unit="Spring Voyage OSS"
# A couple of the unit's template-instantiated agent members (displayName
# per the package's `members:` block). Spot-checked via `agent list` to
# prove the activator wired the inline template agents.
expected_agents=(
    "Ada (engineer)"
    "Drucker (PM)"
)

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
# The OSS package's agents pin the claude-code runtime (provider: anthropic),
# and install now fail-fasts when the required LLM credential is absent. Supply
# a dummy oauth token via --secret so the install completes — one provider slot
# covers every claude-code agent in the package. Validity is only checked at
# agent turn time (out of scope for this install/units-list scenario).
e2e::log "spring package install spring-voyage-oss --connector github=acme/demo@1 --secret anthropic:oauth=*** --input github_owner=acme --input github_repo=demo --input github_installation_id=999"
response="$(e2e::cli --output json package install spring-voyage-oss \
    --connector "github=acme/demo@1" \
    --secret "anthropic:oauth=sk-ant-oat-e2e-dummy" \
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

# --- Verify the single OSS unit exists via unit list ------------------------
e2e::log "spring unit list --output json"
response="$(e2e::cli --output json unit list)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit list succeeds after install"
e2e::expect_contains "\"${parent_unit}\"" "${body}" "unit list includes the OSS unit"

# --- Verify the OSS unit's template agents were wired -----------------------
# The flattened package declares its agent members inline (from templates);
# the unit must carry at least that many agent membership rows. The
# members-list `member` field is the canonical `agent:<hex>`; count those
# (the lone human member carries a `human://` member and a null agentAddress).
e2e::log "spring unit members list '${parent_unit}' --output json"
response="$(e2e::cli --output json unit members list "${parent_unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "OSS unit members list succeeds"
agent_member_count="$(printf '%s' "${body}" | grep -oE '"member":[[:space:]]*"agent:[0-9a-f]{32}"' | wc -l | tr -d '[:space:]')"
if (( agent_member_count >= 7 )); then
    e2e::ok "OSS unit has ${agent_member_count} agent member(s)"
else
    e2e::fail "OSS unit has only ${agent_member_count} agent member(s) — activator did not wire the template agents"
fi

# --- Spot-check the template agents are registered in the directory ---------
e2e::log "spring agent list --output json"
response="$(e2e::cli --output json agent list)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "agent list succeeds after install"
for a in "${expected_agents[@]}"; do
    e2e::expect_contains "${a}" "${body}" "agent list includes template agent '${a}'"
done

e2e::summary
