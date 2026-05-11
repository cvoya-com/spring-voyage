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

# Pre-flight teardown of any leftover engineering-team from a prior failed
# run — see unit-create-from-template.sh for the rationale. `spring unit
# purge` 500s when the unit holds the agents' only membership; force-delete
# the agents (cascading their memberships) then the unit. Best-effort.
_pre_cleanup() {
    local show memberships agent_id unit_hex
    show="$(e2e::cli unit show "${unit}" --output json 2>/dev/null)"
    show="${show%$'\n'*}"
    unit_hex="$(printf '%s' "${show}" | awk -F'"' '/"name":/ { print $4; exit }')"
    if [[ -z "${unit_hex}" || "${unit_hex}" == "${unit}" ]]; then return 0; fi
    memberships="$(e2e::http GET "/api/v1/tenant/units/${unit_hex}/memberships" 2>/dev/null || true)"
    memberships="${memberships%$'\n'*}"
    while IFS= read -r agent_id; do
        [[ -z "${agent_id}" ]] && continue
        e2e::http DELETE "/api/v1/tenant/agents/${agent_id}" >/dev/null 2>&1 || true
    done < <(printf '%s' "${memberships}" | grep -oE '"member":"agent:[0-9a-f]{32}"' | awk -F'[:"]' '{print $5}')
    e2e::http DELETE "/api/v1/tenant/units/${unit_hex}" >/dev/null 2>&1 || true
}
_pre_cleanup
trap '_pre_cleanup' EXIT

# Stub connector binding to satisfy the package's required `github`
# declaration; the fast pool does not exercise real GitHub I/O.
e2e::log "spring package install software-engineering --connector github=acme/repo@1"
response="$(e2e::cli --output json package install software-engineering --connector "github=acme/repo@1")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" != "0" && "${body}" == *"ConnectorBindingMissing"* ]]; then
    # TODO(spring-voyage#cli-e2e-stub-binding): see unit-create-from-template.sh.
    e2e::log "package requires a real connector binding the fast pool cannot supply — skipping scenario."
    exit 0
fi
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
