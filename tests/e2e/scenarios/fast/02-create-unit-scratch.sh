#!/usr/bin/env bash
# Create a minimal unit (no model, no color, no connector) — exercises directory
# registration only. Skips the actor SetMetadataAsync call.
#
# Driven through the `spring` CLI to cover the Kiota-generated client,
# argument parsing, and output formatting in addition to the API contract.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

name="$(e2e::unit_name scratch)"

# Cascading teardown — purges any membership rows then the unit itself. Runs
# on every exit path (success, assertion failure, ctrl-c); swallows errors so
# the scenario's real exit code is preserved.
trap 'e2e::cleanup_unit "${name}"' EXIT

e2e::log "spring unit create ${name} --output json"
response="$(e2e::cli_unit_create --output json "${name}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

if [[ "${code}" == "0" ]]; then
    e2e::ok "minimal unit creation succeeds (exit ${code})"
else
    e2e::fail "minimal unit creation — expected exit 0, got ${code}: ${body:0:500}"
fi
e2e::expect_contains "\"name\": \"${name}\"" "${body}" "create response carries the unit name"

# Confirm the new unit shows up in the list.
e2e::log "spring unit list --output json"
list_response="$(e2e::cli --output json unit list)"
list_code="${list_response##*$'\n'}"
list_body="${list_response%$'\n'*}"
if [[ "${list_code}" == "0" ]]; then
    e2e::ok "unit list succeeds (exit ${list_code})"
else
    e2e::fail "unit list — expected exit 0, got ${list_code}: ${list_body:0:500}"
fi
e2e::expect_contains "\"name\": \"${name}\"" "${list_body}" "list contains the new unit"

e2e::summary
