#!/usr/bin/env bash
# Smoke check: API is reachable and returns a valid response shape.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../_lib.sh"

e2e::log "GET /api/v1/connectors"
response="$(e2e::http GET /api/v1/connectors)"
status="${response##*$'\n'}"
body="${response%$'\n'*}"

e2e::expect_status 200 "${status}" "API responds to /api/v1/connectors"
e2e::expect_contains '[' "${body}" "response body is a JSON array"

e2e::summary
