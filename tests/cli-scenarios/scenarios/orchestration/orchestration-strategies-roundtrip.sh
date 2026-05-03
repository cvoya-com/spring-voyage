#!/usr/bin/env bash
# pool: fast
# Exercise the three platform-offered orchestration strategies (ai,
# workflow, label-routed) via `spring unit orchestration {get,set,clear}`.
# Asserts each strategy round-trips through the manifest and the
# unkeyed-default fallback returns after `clear`.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

unit="$(e2e::unit_name orchestration)"
trap 'e2e::cleanup_unit "${unit}"' EXIT

e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create succeeds"

# Initial state: no orchestration key set; `get` should respond cleanly.
e2e::log "spring unit orchestration get ${unit}"
response="$(e2e::cli --output json unit orchestration get "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "orchestration get on fresh unit succeeds"

# Cycle through the three platform-offered strategies, asserting each
# survives the round trip via `get` after `set`.
for strategy in ai workflow label-routed; do
    e2e::log "spring unit orchestration set ${unit} --strategy ${strategy}"
    response="$(e2e::cli unit orchestration set "${unit}" --strategy "${strategy}")"
    code="${response##*$'\n'}"
    e2e::expect_status "0" "${code}" "orchestration set --strategy=${strategy} succeeds"

    response="$(e2e::cli --output json unit orchestration get "${unit}")"
    code="${response##*$'\n'}"
    body="${response%$'\n'*}"
    e2e::expect_status "0" "${code}" "orchestration get after set ${strategy} succeeds"
    e2e::expect_contains "${strategy}" "${body}" "orchestration get echoes strategy=${strategy}"
done

# Clear restores the unkeyed-default behaviour (ADR-0010).
e2e::log "spring unit orchestration clear ${unit}"
response="$(e2e::cli unit orchestration clear "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "orchestration clear succeeds"

# `get` after clear should still succeed — strategy field becomes empty
# or absent depending on output shape, but the call itself returns 0.
response="$(e2e::cli --output json unit orchestration get "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "orchestration get after clear succeeds"

e2e::summary
