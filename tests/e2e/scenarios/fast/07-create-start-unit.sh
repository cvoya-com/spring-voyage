#!/usr/bin/env bash
# Create a unit from template, start it, verify it reaches Running status.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

name="$(e2e::unit_name start-test)"
trap 'e2e::cleanup_unit "${name}"' EXIT

# Create from template (starts in Stopped per #369 since template provides model)
e2e::log "spring unit create --from-template software-engineering/engineering-team --name ${name}"
response="$(e2e::cli_unit_create --output json --from-template software-engineering/engineering-team --name "${name}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create from template succeeds"

# Start the unit
e2e::log "spring unit start ${name}"
response="$(e2e::cli unit start "${name}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit start succeeds"

# Verify status is Running (or Starting — may depend on timing)
e2e::log "spring unit status ${name}"
response="$(e2e::cli --output json unit status "${name}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit status check succeeds"
# Accept Running or Starting — the transition may be async
if [[ "${body}" == *"Running"* ]] || [[ "${body}" == *"Starting"* ]]; then
    e2e::ok "unit is Running or Starting"
else
    e2e::fail "unit status — expected Running or Starting, got: ${body:0:200}"
fi

e2e::summary
