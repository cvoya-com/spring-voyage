#!/usr/bin/env bash
# pool: fast
# Install the engineering-team unit via `spring package install` and verify
# the unit reports a sensible status. The pre-#1583 form of this scenario
# used `unit create --from-template` (now deleted); the v0.1 install path
# is `spring package install`.
#
# The actor transition table (#939) intentionally forbids Draft->Starting:
# units must pass through Validating->Stopped before they can be started.
# Package-installed units may land in Draft (no unit-level model triggers
# auto-validation) or Stopped (validation completed). Both are acceptable
# for this fast-pool readiness check; we just assert the status command
# works and the readiness shape is sensible.
#
# The full start path (Draft->Stopped->Starting->Running) requires a
# resolvable credential and a running container probe — out of scope.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

unit="engineering-team"
trap 'e2e::cleanup_unit "${unit}"' EXIT

e2e::log "spring package install software-engineering"
response="$(e2e::cli --output json package install software-engineering)"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "package install succeeds"

# Verify status command works — it should return a JSON envelope with
# `status` and `isReady` fields regardless of which terminal state the
# unit lands in (Draft / Stopped / Validating).
e2e::log "spring unit status ${unit}"
response="$(e2e::cli --output json unit status "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit status check succeeds"

# Readiness/status shape sanity check: the response must carry both
# `status` and `isReady` keys. Don't pin to a specific value because the
# actor may still be in Validating when this scenario runs.
e2e::expect_contains '"status"' "${body}" "status response includes status field"
e2e::expect_contains '"isReady"' "${body}" "status response includes isReady field"

e2e::summary
