#!/usr/bin/env bash
# Create a unit from template and verify its status and readiness.
#
# #369 originally expected template-created units to auto-start in Stopped
# state (since the template provides agent models). The actor transition
# table (#939) intentionally forbids Draft->Starting: units must pass
# through Validating->Stopped before they can be started. Template creation
# does not set a unit-level model/provider, so IsFullyConfiguredForValidation
# returns false and no auto-validation is triggered on creation.
#
# This scenario now asserts:
#   1. Unit can be created from template (exit 0).
#   2. Unit status command succeeds (exit 0).
#   3. Unit is in Draft state with isReady=true and no missing requirements
#      — confirming the template wired up all agents correctly and the
#      readiness check passes, even though the unit hasn't been validated yet.
#
# The start path (Draft->Stopped->Starting->Running) requires a resolvable
# credential and a running container probe — out of scope for the fast pool.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

name="$(e2e::unit_name start-test)"
trap 'e2e::cleanup_unit "${name}"' EXIT

# Create from template
e2e::log "spring unit create --from-template software-engineering/engineering-team --name ${name}"
response="$(e2e::cli_unit_create --output json --from-template software-engineering/engineering-team --name "${name}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create from template succeeds"

# Verify status command works
e2e::log "spring unit status ${name}"
response="$(e2e::cli --output json unit status "${name}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit status check succeeds"

# Template-created units land in Draft (no unit-level model triggers auto-validation).
# isReady=true means all readiness requirements are met — template wired agents correctly.
if [[ "${body}" == *'"status": "Draft"'* ]]; then
    e2e::ok "unit is in Draft state (expected: no unit-level model to trigger auto-validation)"
else
    e2e::fail "unit status — expected Draft, got: ${body:0:200}"
fi

if [[ "${body}" == *'"isReady": true'* ]]; then
    e2e::ok "unit reports isReady=true (template agents wired correctly)"
else
    e2e::fail "unit readiness — expected isReady=true, got: ${body:0:200}"
fi

e2e::summary
