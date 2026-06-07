#!/usr/bin/env bash
# pool: fast
# Install the `hello-world` catalog package and verify the resulting unit
# reports a sensible status. The pre-#1583 form of this scenario used
# `unit create --from-template` (now deleted); the v0.1 install path is
# `spring package install`.
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
#
# Why hello-world (#2127): every other catalog package declares a required
# `github` connector binding which forced the fast pool to stub
# `--connector github=acme/repo@1` just to clear `ConnectorBindingMissing`.
# `hello-world` ships with no `requires:` block (#2115 / PR #2125), so the
# install path is exercised end-to-end without a stub binding.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

unit="hello-world"

# Pre-flight teardown of any leftover hello-world unit(s) from a prior run.
# The package's unit name is fixed (`hello-world`), so a failed cleanup leaves
# a unit behind; once TWO exist, `unit status hello-world` fails with
# "Multiple units match". Force-delete EVERY unit whose displayName is the
# slug, plus its agents. `spring unit purge` 500s when the unit holds an
# agent's only membership, so force-delete the agents (cascading their
# memberships) then the unit. Best-effort; a clean tenant yields no matches.
_pre_cleanup() {
    local units_body unit_hex memberships agent_id
    units_body="$(e2e::http GET "/api/v1/tenant/units" 2>/dev/null || true)"
    units_body="${units_body%$'\n'*}"
    while IFS= read -r unit_hex; do
        [[ -z "${unit_hex}" ]] && continue
        memberships="$(e2e::http GET "/api/v1/tenant/units/${unit_hex}/memberships" 2>/dev/null || true)"
        memberships="${memberships%$'\n'*}"
        while IFS= read -r agent_id; do
            [[ -z "${agent_id}" ]] && continue
            e2e::http DELETE "/api/v1/tenant/agents/${agent_id}" >/dev/null 2>&1 || true
        done < <(printf '%s' "${memberships}" | grep -oE '"member":"agent:[0-9a-f]{32}"' | awk -F'[:"]' '{print $5}')
        e2e::http DELETE "/api/v1/tenant/units/${unit_hex}?force=true" >/dev/null 2>&1 || true
    done < <(printf '%s' "${units_body}" | jq -r '.[] | select(.displayName=="hello-world") | .name' 2>/dev/null)
}
_pre_cleanup
trap '_pre_cleanup' EXIT

# Install hello-world with no --connector flag — the package declares no
# `requires:` block on either side, so the install pipeline accepts it
# without a binding. It does pin the claude-code runtime (provider:
# anthropic), and install now fail-fasts when the required LLM credential is
# absent; supply a dummy oauth token via --secret so the install completes.
# Validity is only checked at agent turn time, which this status-shape
# scenario never reaches.
e2e::log "spring package install hello-world --secret anthropic:oauth=***"
response="$(e2e::cli --output json package install hello-world --secret "anthropic:oauth=sk-ant-oat-e2e-dummy")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
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
