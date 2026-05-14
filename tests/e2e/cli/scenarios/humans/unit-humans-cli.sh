#!/usr/bin/env bash
# pool: fast
# `spring unit humans add|remove|list` (#454).
#
# Pre-#1583 this scenario also exercised `spring unit create-from-template`
# and the legacy `--from-template` deprecation warning. Both verbs were
# deleted in 213f39bc — the v0.1 surface is `spring package install`.
# Template-installed humans are now covered by the package-install
# scenarios; this scenario focuses purely on the humans CRUD verb cluster.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

humans_unit="$(e2e::unit_name humans)"

cleanup() {
    # No `|| true` — the helpers swallow purge errors internally and preserve
    # the caller's `$?` (#1030). Wrapping with `|| true` would reset $? to 0
    # and mask a failing e2e::summary, defeating the #1030 fix (#1044).
    e2e::cleanup_unit "${humans_unit}"
}
trap cleanup EXIT

# --- humans add / list / remove (#454) ---------------------------------------

e2e::log "spring unit create ${humans_unit}"
response="$(e2e::cli_unit_create --output json "${humans_unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create (humans scenario) succeeds"

# Keep the shape of the exact invocation referenced in docs/guide/observing.md.
# `add` resolves the username to a stable UUID via IHumanIdentityResolver and
# returns the UUID in the response body — the unit actor only stores UUIDs,
# not usernames, so the rest of this scenario asserts on the returned UUID
# rather than the original "alice" string.
e2e::log "spring unit humans add ${humans_unit} alice --permission owner --notifications slack,email --output json"
response="$(e2e::cli --output json unit humans add "${humans_unit}" alice --permission owner --notifications slack,email)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "humans add (owner) succeeds"
e2e::expect_contains "\"humanId\"" "${body}" "humans add JSON carries humanId"
e2e::expect_contains "\"permission\": \"Owner\"" "${body}" "humans add response echoes Owner permission"

# Capture the UUID so we can assert presence in the list. The shape is
# `{"humanId":"<uuid>", ...}` so a small grep + sed is enough — no jq.
alice_uuid="$(printf '%s' "${body}" | grep -o '"humanId":[[:space:]]*"[^"]*"' | head -n 1 | sed -E 's/.*"humanId":[[:space:]]*"([^"]+)".*/\1/')"
if [[ -n "${alice_uuid}" ]]; then
    e2e::ok "captured alice's UUID: ${alice_uuid}"
else
    e2e::fail "could not capture humanId UUID from add response"
fi

# list returns the entry we just added.
e2e::log "spring unit humans list ${humans_unit} --output json"
response="$(e2e::cli --output json unit humans list "${humans_unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "humans list succeeds"
if [[ -n "${alice_uuid}" ]]; then
    e2e::expect_contains "${alice_uuid}" "${body}" "humans list includes alice's UUID"
fi

# remove then re-list — the server returns 204 on DELETE regardless of prior
# presence, so a second remove is a no-op.
e2e::log "spring unit humans remove ${humans_unit} alice"
response="$(e2e::cli unit humans remove "${humans_unit}" alice)"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "humans remove succeeds"

e2e::log "spring unit humans remove ${humans_unit} alice (second time — idempotent)"
response="$(e2e::cli unit humans remove "${humans_unit}" alice)"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "humans remove is idempotent"

e2e::log "spring unit humans list ${humans_unit} --output json (expect no alice)"
response="$(e2e::cli --output json unit humans list "${humans_unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "humans list after remove succeeds"
# The list may contain the auto-created owner from unit start-up, but it must
# not contain the alice entry we just removed (asserted by UUID).
if [[ -n "${alice_uuid}" ]] && printf '%s' "${body}" | grep -q "${alice_uuid}"; then
    e2e::fail "humans list still contains alice's UUID (${alice_uuid}) after remove"
else
    e2e::ok "humans list no longer contains alice's UUID"
fi

e2e::summary
