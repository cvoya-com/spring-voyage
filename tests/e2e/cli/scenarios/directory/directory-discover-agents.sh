#!/usr/bin/env bash
# pool: fast
# Discover units via the tenant-wide expertise directory.
# Stamps a unique-per-run domain on a unit's expertise so the directory
# search can find it without colliding with other tests.
#
# Per InMemoryExpertiseSearch boundary semantics, external callers see only
# unit-projected entries (agent-level hits are hidden by default). The CLI
# runs as an external caller so we set the expertise on the unit, which is
# what an operator would also reach for when publishing capabilities.
#
# Asserts:
#   - `spring directory list` returns a non-empty list and includes the
#     seeded unit's slug.
#   - `spring directory search <token>` matches the unit by domain text.
#   - `spring directory show <slug>` returns the entry detail with the
#     seeded domain.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

unit="$(e2e::unit_name dir-host)"
# Run-scoped domain so concurrent runs don't shadow each other's hits.
domain="testing/${E2E_RUN_ID}-discovery"

cleanup() {
    e2e::cleanup_unit "${unit}"
}
trap cleanup EXIT

# --- Setup: unit + expertise ------------------------------------------------
e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create succeeds"

e2e::log "spring unit expertise set ${unit} --domain '${domain}:expert:CLI e2e discovery probe'"
response="$(e2e::cli unit expertise set "${unit}" --domain "${domain}:expert:CLI e2e discovery probe")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit expertise set succeeds"

# --- Discover via directory list --------------------------------------------
e2e::log "spring directory list --output json"
response="$(e2e::cli --output json directory list)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "directory list succeeds"
e2e::expect_contains "${unit}" "${body}" "directory list includes the seeded unit"

# --- Discover via directory search ------------------------------------------
e2e::log "spring directory search '${E2E_RUN_ID}-discovery' --output json"
response="$(e2e::cli --output json directory search "${E2E_RUN_ID}-discovery")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "directory search succeeds"
e2e::expect_contains "${unit}" "${body}" "directory search query matches the unit"

# --- Show the unit's directory entry by slug --------------------------------
# `directory show` takes the entry slug — the slugified domain name, not
# the unit's path. The slug shape is `<domain-with-slashes-collapsed>`;
# see ExpertiseSkillNaming.Slugify in InMemoryExpertiseSearch.
slug="$(printf '%s' "${body}" | grep -o '"slug":[[:space:]]*"[^"]*"' | head -1 | sed -E 's/.*"slug":[[:space:]]*"([^"]+)".*/\1/')"
if [[ -n "${slug}" ]]; then
    e2e::ok "captured directory slug from search response: ${slug}"
    e2e::log "spring directory show ${slug} --output json"
    response="$(e2e::cli --output json directory show "${slug}")"
    code="${response##*$'\n'}"
    body="${response%$'\n'*}"
    e2e::expect_status "0" "${code}" "directory show succeeds"
    e2e::expect_contains "${unit}" "${body}" "directory show entry mentions the unit"
else
    e2e::fail "directory search returned no slug to show"
fi

e2e::summary
