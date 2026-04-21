#!/usr/bin/env bash
# Create a unit with model + color — exercises UnitActor.SetMetadataAsync via
# the Dapr actor proxy. This is the flow that surfaces actor-wiring bugs
# (type-name mismatch, missing sidecar, data-contract serialization,
# placement routing).
#
# #315 exposed `--model` and `--color` on `spring unit create`, so this
# scenario is now fully CLI-driven.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

name="$(e2e::unit_name with-model)"

# Cascading teardown — purges the unit whether or not the call below returned
# an id. `unit purge` resolves by name (address path), which is the same
# field we pass into the create call, so we don't need to parse the response.
trap 'e2e::cleanup_unit "${name}"' EXIT

e2e::log "spring unit create ${name} --model claude-sonnet-4-6 --color #6366f1"
response="$(e2e::cli_unit_create --output json "${name}" --model claude-sonnet-4-6 --color "#6366f1")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

e2e::expect_status "0" "${code}" "unit-with-model creation succeeds via CLI"
e2e::expect_contains "\"model\": \"claude-sonnet-4-6\"" "${body}" "response carries the model"
e2e::expect_contains "\"color\": \"#6366f1\"" "${body}" "response carries the color"

e2e::summary
