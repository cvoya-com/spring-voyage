#!/usr/bin/env bash
# pool: fast
# Explicit `delete` verb coverage for both units and agents — complementary
# to `unit purge` (cascading) and `agent purge` (cascading), which are
# already covered by unit-membership-roundtrip. The plain `delete` verbs are
# the ones a user reaches for when they know the artefact is empty / safe
# to drop and the cascading semantics aren't needed.
#
# Asserts:
#   - `agent delete` on a standalone (no-membership) agent succeeds
#   - `unit delete` on a Draft unit succeeds (no force flag, no purge needed)
#   - Both deletes are idempotent at the API level via 404 on second attempt
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

# Two units so the agent-delete leg has a parent to attach to (agent create
# requires --unit per #744). The host-unit's purge is the trap fall-back if
# any subsequent step blows up before we get to the explicit delete.
host_unit="$(e2e::unit_name del-host)"
solo_unit="$(e2e::unit_name del-solo)"
agent="$(e2e::agent_name del-target)"

cleanup() {
    e2e::cleanup_unit "${host_unit}"
    e2e::cleanup_unit "${solo_unit}"
    e2e::cleanup_agent "${agent}"
}
trap cleanup EXIT

# --- Setup ------------------------------------------------------------------
e2e::log "spring unit create ${host_unit} (host for the test agent)"
response="$(e2e::cli_unit_create --output json "${host_unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "host unit create succeeds"

e2e::log "spring unit create ${solo_unit} (target for unit delete)"
response="$(e2e::cli_unit_create --output json "${solo_unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "solo unit create succeeds"

e2e::log "spring agent create ${agent} --unit ${host_unit}"
response="$(e2e::cli_agent_create --output json "${agent}" --unit "${host_unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "agent create succeeds"

# --- agent delete -----------------------------------------------------------
# `agent delete` removes the agent record (and its membership rows) without
# the cascading semantics of `agent purge`. Even when the agent has a
# single membership it is the last-unit case; delete still completes.
e2e::log "spring agent delete ${agent}"
response="$(e2e::cli agent delete "${agent}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "agent delete succeeds"

# Second delete: the API returns 404. The CLI's exit code for 404 is
# non-zero (1 or 2). Asserting non-zero proves the operation wasn't a
# silent no-op the way it would be if the row were soft-deleted but not
# unlinked from the directory.
e2e::log "spring agent delete ${agent} (second time — expect non-zero)"
response="$(e2e::cli agent delete "${agent}" 2>&1 || true)"
code="${response##*$'\n'}"
if [[ "${code}" != "0" ]]; then
    e2e::ok "agent delete on missing agent exits non-zero (code=${code})"
else
    e2e::fail "agent delete on missing agent returned 0 — expected non-zero"
fi

# --- unit delete ------------------------------------------------------------
e2e::log "spring unit delete ${solo_unit}"
response="$(e2e::cli unit delete "${solo_unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit delete on Draft unit succeeds"

e2e::log "spring unit delete ${solo_unit} (second time — expect non-zero)"
response="$(e2e::cli unit delete "${solo_unit}" 2>&1 || true)"
code="${response##*$'\n'}"
if [[ "${code}" != "0" ]]; then
    e2e::ok "unit delete on missing unit exits non-zero (code=${code})"
else
    e2e::fail "unit delete on missing unit returned 0 — expected non-zero"
fi

e2e::summary
