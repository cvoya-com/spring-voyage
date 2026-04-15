#!/usr/bin/env bash
# Create a unit with model + color — exercises UnitActor.SetMetadataAsync via
# Dapr actor proxy. This is the flow that surfaces actor-wiring bugs (type-name
# mismatch, missing sidecar, data-contract serialization, placement routing).
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../_lib.sh"

name="e2e-with-model-$(date +%s%N | tail -c 6)"
body="{\"name\":\"${name}\",\"model\":\"claude-sonnet-4-20250514\",\"color\":\"#6366f1\"}"
e2e::log "POST /api/v1/units ${body}"
response="$(e2e::http POST /api/v1/units "${body}")"
status="${response##*$'\n'}"
resp_body="${response%$'\n'*}"

if [[ "${status}" == "200" || "${status}" == "201" ]]; then
    e2e::ok "unit-with-model creation succeeds (status ${status})"
else
    e2e::fail "unit-with-model creation — expected 200/201, got ${status}: ${resp_body:0:500}"
fi
e2e::expect_contains "\"model\":\"claude-sonnet-4-20250514\"" "${resp_body}" "response carries the model"
e2e::expect_contains "\"color\":\"#6366f1\"" "${resp_body}" "response carries the color"

id="$(printf '%s' "${resp_body}" | grep -oE '"id":"[^"]*"' | head -1 | cut -d'"' -f4 || true)"
if [[ -n "${id}" ]]; then
    e2e::log "DELETE /api/v1/units/${id} (cleanup)"
    e2e::http DELETE "/api/v1/units/${id}" > /dev/null || true
fi

e2e::summary
