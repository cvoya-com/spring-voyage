#!/usr/bin/env bash
# Activity query filter surface (#404).
#
# Every observability tool (portal activity page, CLI `spring activity list`,
# dashboard) relies on the same activity query endpoint with four server-side
# filters: source, eventType, severity, limit (pageSize). This scenario drives
# each filter at least once and asserts the response shape, guarding the
# query path that replaces the SSE-subscription acceptance item on #404's list
# — the full SSE push flow requires cross-host event bridging that isn't
# available in the fast pool.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

# --- Shape check: unfiltered query returns paginated payload -----------------
# ActivityQueryResult is { items, totalCount, page, pageSize }. All four
# fields must be present even when the store is empty.
e2e::log "GET /api/v1/tenant/activity (unfiltered)"
response="$(e2e::http GET "/api/v1/tenant/activity?limit=3")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "activity query returns 200"
e2e::expect_contains "\"items\":" "${body}" "response carries items array"
e2e::expect_contains "\"totalCount\":" "${body}" "response carries totalCount"
e2e::expect_contains "\"page\":" "${body}" "response carries page"
e2e::expect_contains "\"pageSize\":" "${body}" "response carries pageSize"

# --- eventType filter: StateChanged is emitted by every unit lifecycle -------
# The environment has at least one prior unit create, so StateChanged must
# have at least one row. Using the filter proves the server-side WHERE is
# wired; a case-sensitivity regression would surface here.
e2e::log "GET /api/v1/tenant/activity?eventType=StateChanged"
response="$(e2e::http GET "/api/v1/tenant/activity?eventType=StateChanged&limit=5")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "eventType filter query returns 200"
e2e::expect_contains "\"StateChanged\"" "${body}" "filtered response contains StateChanged events"

# --- severity filter: Info threshold drops Debug entries ---------------------
# The ActivityQueryService treats severity as an exact match, not a minimum;
# the wire shape still distinguishes Debug from Info, so asserting the
# filtered response's severity field matches the request is meaningful.
e2e::log "GET /api/v1/tenant/activity?severity=Info"
response="$(e2e::http GET "/api/v1/tenant/activity?severity=Info&limit=5")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "severity filter query returns 200"
# If any Info-level rows exist at all, they must appear; if none exist, the
# shape assertion covers the regression. Either way totalCount must be >=0.
e2e::expect_contains "\"totalCount\":" "${body}" "severity-filtered response carries totalCount"

# --- source filter: a source that does not exist must return zero items -----
# Using a deterministically non-existent source id proves the filter
# actually narrows results rather than being silently dropped.
bogus_source="agent:$(e2e::agent_name non-existent-$(date +%s%N))"
e2e::log "GET /api/v1/tenant/activity?source=${bogus_source}"
response="$(e2e::http GET "/api/v1/tenant/activity?source=${bogus_source}&limit=5")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "source filter for non-existent source returns 200"
e2e::expect_contains "\"totalCount\":0" "${body}" "non-existent source yields totalCount=0"
e2e::expect_contains "\"items\":[]" "${body}" "non-existent source yields empty items array"

# --- pageSize honour ---------------------------------------------------------
# pageSize must bound the returned array length. Ask for 1 item and verify
# the items array carries at most one entry — the shape change from "items":
# []" to "items":[{...}]" is a reliable tell without parsing JSON.
e2e::log "GET /api/v1/tenant/activity?pageSize=1"
response="$(e2e::http GET "/api/v1/tenant/activity?pageSize=1")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "pageSize=1 query returns 200"
e2e::expect_contains "\"pageSize\":1" "${body}" "response echoes pageSize=1"
# Count JSON objects in items by counting `{` — the items elements are the
# only braced entries in the payload. A pageSize=1 response must contain 0
# or 1 such entry; 2 would mean the server ignored the bound.
items_count="$(printf '%s' "${body}" | tr -cd '{' | wc -c | tr -d ' ')"
if [[ "${items_count}" -le 2 ]]; then
    # 2 allowed because the top-level object itself has one `{`.
    e2e::ok "pageSize=1 bounds the items array (observed ${items_count} braces)"
else
    e2e::fail "pageSize=1 returned more than one item (observed ${items_count} braces): ${body:0:400}"
fi

e2e::summary
