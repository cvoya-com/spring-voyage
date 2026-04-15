#!/usr/bin/env bash
# Create a unit from the engineering-team template — exercises skill-bundle
# resolver + validator + connector binding preview.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../_lib.sh"

e2e::log "GET /api/v1/packages/templates (discover templates)"
response="$(e2e::http GET /api/v1/packages/templates)"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status 200 "${status}" "templates endpoint returns 200"
e2e::expect_contains 'engineering-team' "${body}" "engineering-team template is listed"

display_name="e2e-from-template-$(date +%s%N | tail -c 6)"
# CreateUnitFromTemplateRequest: {Package, Name (= template basename), DisplayName?, ...}
payload="{\"package\":\"software-engineering\",\"name\":\"engineering-team\",\"displayName\":\"${display_name}\"}"
e2e::log "POST /api/v1/units/from-template ${payload}"
response="$(e2e::http POST /api/v1/units/from-template "${payload}")"
status="${response##*$'\n'}"
resp_body="${response%$'\n'*}"

if [[ "${status}" == "200" || "${status}" == "201" ]]; then
    e2e::ok "from-template creation succeeds (status ${status})"
else
    e2e::fail "from-template creation — expected 200/201, got ${status}: ${resp_body:0:500}"
fi
e2e::expect_contains '"warnings"' "${resp_body}" "response includes warnings array (may list unresolved bundle tools)"

id="$(printf '%s' "${resp_body}" | grep -oE '"id":"[^"]*"' | head -1 | cut -d'"' -f4 || true)"
if [[ -n "${id}" ]]; then
    e2e::log "DELETE /api/v1/units/${id} (cleanup)"
    e2e::http DELETE "/api/v1/units/${id}" > /dev/null || true
fi

e2e::summary
