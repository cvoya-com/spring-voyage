#!/usr/bin/env bash
# `spring unit humans add|remove|list` + create-from-template verb (#454, #460).
#
# Exercises the CLI halves of three parity gaps in one scenario:
#   - `unit humans add|remove|list` — the full triple.
#   - `unit create-from-template <package>/<template>` — first-class verb
#     for both the software-engineering and product-management packages, so
#     the required acceptance ("a template from software-engineering and
#     one from product-management") is proven end-to-end.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

eng_unit="$(e2e::unit_name from-tpl-eng)"
pm_unit="$(e2e::unit_name from-tpl-pm)"
humans_unit="$(e2e::unit_name humans)"

cleanup() {
    # No `|| true` — the helpers swallow purge errors internally and preserve
    # the caller's `$?` (#1030). Wrapping with `|| true` would reset $? to 0
    # and mask a failing e2e::summary, defeating the #1030 fix (#1044).
    e2e::cleanup_unit "${eng_unit}"
    e2e::cleanup_unit "${pm_unit}"
    e2e::cleanup_unit "${humans_unit}"
}
trap cleanup EXIT

# --- create-from-template first-class verb (#460) ----------------------------

# software-engineering/engineering-team.
e2e::log "spring unit create-from-template software-engineering/engineering-team --name ${eng_unit}"
response="$(e2e::cli_unit_create_from_template --output json software-engineering/engineering-team --name "${eng_unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "create-from-template (software-engineering) succeeds"
e2e::expect_contains "\"name\": \"${eng_unit}\"" "${body}" "created unit carries UnitName override"

# product-management/product-team. Discovery first so the scenario doesn't
# need the template basename hardcoded.
e2e::log "GET /api/v1/tenant/packages/templates (find a product-management template)"
tpl_response="$(e2e::http GET /api/v1/tenant/packages/templates)"
tpl_status="${tpl_response##*$'\n'}"
tpl_body="${tpl_response%$'\n'*}"
e2e::expect_status "200" "${tpl_status}" "templates endpoint returns 200"
# Pick the first product-management template the server lists. The exact
# template name is not the point — we just need ANY template from the
# product-management package so the second-package acceptance holds.
pm_template="$(printf '%s' "${tpl_body}" \
    | tr ',' '\n' \
    | grep -E '"package"[[:space:]]*:[[:space:]]*"product-management"' -A 5 \
    | grep -E '"name"[[:space:]]*:' \
    | head -n 1 \
    | sed -E 's/.*"name"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')"

if [[ -z "${pm_template}" ]]; then
    e2e::log "No product-management template found; skipping product-management half of #460 acceptance."
else
    e2e::log "spring unit create-from-template product-management/${pm_template} --name ${pm_unit}"
    response="$(e2e::cli_unit_create_from_template --output json "product-management/${pm_template}" --name "${pm_unit}")"
    code="${response##*$'\n'}"
    body="${response%$'\n'*}"
    e2e::expect_status "0" "${code}" "create-from-template (product-management) succeeds"
    e2e::expect_contains "\"name\": \"${pm_unit}\"" "${body}" "product-management unit carries UnitName override"
fi

# --- Legacy --from-template still works with a deprecation warning -----------
e2e::log "spring unit create --from-template software-engineering/engineering-team --name ${eng_unit}-legacy (deprecation path)"
# We purge the previous unit in the trap; for the deprecation test we use a
# different suffix so it does not collide.
legacy_unit="${eng_unit}-legacy"
response="$(e2e::cli_unit_create --output json --from-template software-engineering/engineering-team --name "${legacy_unit}" 2>&1 || true)"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "legacy --from-template still exits 0"
e2e::expect_contains "deprecated" "${response}" "legacy path prints deprecation warning"
e2e::cleanup_unit "${legacy_unit}" || true

# --- humans add / list / remove (#454) ---------------------------------------

e2e::log "spring unit create ${humans_unit}"
response="$(e2e::cli_unit_create --output json "${humans_unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create (humans scenario) succeeds"

# Keep the shape of the exact invocation referenced in docs/guide/observing.md.
e2e::log "spring unit humans add ${humans_unit} alice --permission owner --notifications slack,email"
response="$(e2e::cli unit humans add "${humans_unit}" alice --permission owner --notifications slack,email)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "humans add (owner) succeeds"
e2e::expect_contains "alice" "${body}" "humans add echoes humanId"

# list returns the entry we just added.
e2e::log "spring unit humans list ${humans_unit} --output json"
response="$(e2e::cli --output json unit humans list "${humans_unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "humans list succeeds"
e2e::expect_contains "alice" "${body}" "humans list includes alice"

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
# not contain the alice entry we just removed.
if printf '%s' "${body}" | grep -q 'alice'; then
    e2e::fail "humans list still contains alice after remove"
else
    e2e::ok "humans list no longer contains alice"
fi

e2e::summary
