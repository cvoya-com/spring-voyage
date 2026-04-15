#!/usr/bin/env bash
# Create a minimal unit (no model, no color, no connector) — exercises directory
# registration only. Skips the actor SetMetadataAsync call.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../_lib.sh"

name="e2e-scratch-$(date +%s%N | tail -c 6)"
e2e::log "POST /api/v1/units {name:${name}}"
response="$(e2e::http POST /api/v1/units "{\"name\":\"${name}\"}")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"

if [[ "${status}" == "200" || "${status}" == "201" ]]; then
    e2e::ok "minimal unit creation succeeds (status ${status})"
else
    e2e::fail "minimal unit creation — expected 200/201, got ${status}: ${body:0:300}"
fi
e2e::expect_contains "\"name\":\"${name}\"" "${body}" "response echoes the unit name"

# Best-effort cleanup: extract id and delete.
id="$(printf '%s' "${body}" | grep -oE '"id":"[^"]*"' | head -1 | cut -d'"' -f4 || true)"
if [[ -n "${id}" ]]; then
    e2e::log "DELETE /api/v1/units/${id} (cleanup)"
    e2e::http DELETE "/api/v1/units/${id}" > /dev/null || true
fi

e2e::summary
