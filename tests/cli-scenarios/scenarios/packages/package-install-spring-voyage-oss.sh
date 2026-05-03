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

# The OSS package's units have fixed names declared in their manifests.
# We clean them all up via the trap because the package's parent unit
# (spring-voyage-oss) cascades its sub-units on purge.
parent_unit="spring-voyage-oss"
sub_units=(
    sv-oss-software-engineering
    sv-oss-design
    sv-oss-product-management
    sv-oss-program-management
)

cleanup() {
    # Purge sub-units first (cascade may not catch them all if their parent
    # edge wasn't fully wired). Errors from individual purges are swallowed
    # by e2e::cleanup_unit so they don't mask the scenario's exit code.
    for u in "${sub_units[@]}"; do e2e::cleanup_unit "${u}"; done
    e2e::cleanup_unit "${parent_unit}"
}
trap cleanup EXIT

# --- Install ----------------------------------------------------------------
# Required inputs: github_owner, github_repo, github_installation_id.
# We supply placeholder values — the install completes Phase 1 (staging)
# and Phase 2 (activation) without making any GitHub API calls because
# bind-on-install is connector-side and the dispatcher only contacts
# GitHub when a unit is actually started.
e2e::log "spring package install spring-voyage-oss --input github_owner=acme --input github_repo=demo --input github_installation_id=999"
response="$(e2e::cli --output json package install spring-voyage-oss \
    --input github_owner=acme \
    --input github_repo=demo \
    --input github_installation_id=999)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
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
e2e::log "spring unit members list sv-oss-software-engineering --output json"
response="$(e2e::cli --output json unit members list sv-oss-software-engineering)"
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
