#!/usr/bin/env bash
# `spring secret <verb>` end-to-end CRUD + rotate + prune roundtrip (#432).
#
# Exercises the seven verbs landed by PR-432 against a throwaway unit:
#   create -> list -> rotate -> versions -> prune -> get -> delete
# Each verb is invoked with --output json so the scenario can check the
# response shape without regexing human-formatted text. The secrets CRUD
# item deferred from #404 lights up here — the scenario proves the full
# lifecycle works end-to-end through the CLI, not just the HTTP surface.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

unit="$(e2e::unit_name secret-cli)"
secret_name="pr432-roundtrip"

trap 'e2e::cleanup_unit "${unit}"' EXIT

e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create succeeds"

# --- create a unit-scoped pass-through secret --------------------------------
e2e::log "spring secret create --scope unit --unit ${unit} ${secret_name} --value v1-plaintext"
response="$(e2e::cli --output json secret create --scope unit --unit "${unit}" "${secret_name}" --value v1-plaintext)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "secret create succeeds"
e2e::expect_contains "\"name\": \"${secret_name}\"" "${body}" "create echoes secret name"
e2e::expect_contains "\"scope\": \"Unit\"" "${body}" "create echoes Unit scope"

# --- list: must contain the new secret ---------------------------------------
e2e::log "spring secret list --scope unit --unit ${unit}"
response="$(e2e::cli --output json secret list --scope unit --unit "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "secret list succeeds"
e2e::expect_contains "\"name\": \"${secret_name}\"" "${body}" "list includes newly-created secret"

# --- rotate: append a new version -------------------------------------------
e2e::log "spring secret rotate --scope unit --unit ${unit} ${secret_name} --value v2-plaintext"
response="$(e2e::cli --output json secret rotate --scope unit --unit "${unit}" "${secret_name}" --value v2-plaintext)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "secret rotate succeeds"
e2e::expect_contains "\"version\": 2" "${body}" "rotate returns version 2"

# rotate once more so the prune step has >1 non-current version to drop.
e2e::log "spring secret rotate --scope unit --unit ${unit} ${secret_name} --value v3-plaintext"
response="$(e2e::cli --output json secret rotate --scope unit --unit "${unit}" "${secret_name}" --value v3-plaintext)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "second rotate succeeds"
e2e::expect_contains "\"version\": 3" "${body}" "second rotate returns version 3"

# --- versions: three retained ------------------------------------------------
e2e::log "spring secret versions --scope unit --unit ${unit} ${secret_name}"
response="$(e2e::cli --output json secret versions --scope unit --unit "${unit}" "${secret_name}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "secret versions succeeds"
e2e::expect_contains "\"version\": 1" "${body}" "versions lists v1"
e2e::expect_contains "\"version\": 2" "${body}" "versions lists v2"
e2e::expect_contains "\"version\": 3" "${body}" "versions lists v3"

# --- prune: keep 1; drops v1 and v2 ------------------------------------------
e2e::log "spring secret prune --scope unit --unit ${unit} ${secret_name} --keep 1"
response="$(e2e::cli --output json secret prune --scope unit --unit "${unit}" "${secret_name}" --keep 1)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "secret prune succeeds"
e2e::expect_contains "\"keep\": 1" "${body}" "prune echoes keep=1"
e2e::expect_contains "\"pruned\": 2" "${body}" "prune reports 2 versions removed"

# --- get: metadata for the current version (plaintext NEVER returned) --------
e2e::log "spring secret get --scope unit --unit ${unit} ${secret_name}"
response="$(e2e::cli --output json secret get --scope unit --unit "${unit}" "${secret_name}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "secret get succeeds"
e2e::expect_contains "\"version\": 3" "${body}" "get shows current version (3)"
e2e::expect_contains "\"isCurrent\": true" "${body}" "get flags the current version"
if printf '%s' "${body}" | grep -q 'v3-plaintext'; then
    e2e::fail "get leaked plaintext into metadata response"
else
    e2e::ok "get never returns plaintext"
fi

# --- delete: remove every version --------------------------------------------
e2e::log "spring secret delete --scope unit --unit ${unit} ${secret_name}"
response="$(e2e::cli secret delete --scope unit --unit "${unit}" "${secret_name}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "secret delete succeeds"

# list again: the secret must be gone.
e2e::log "spring secret list --scope unit --unit ${unit} (expect no ${secret_name})"
response="$(e2e::cli --output json secret list --scope unit --unit "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "post-delete list succeeds"
if printf '%s' "${body}" | grep -q "\"name\": \"${secret_name}\""; then
    e2e::fail "secret still present after delete"
else
    e2e::ok "secret removed after delete"
fi

# --- tenant-scoped create + delete (narrower surface, just prove the path) ---
tenant_secret="pr432-tenant-${E2E_RUN_ID}"
e2e::log "spring secret create --scope tenant ${tenant_secret} --value tenant-v1"
response="$(e2e::cli --output json secret create --scope tenant "${tenant_secret}" --value tenant-v1)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "tenant-scoped create succeeds"
e2e::expect_contains "\"scope\": \"Tenant\"" "${body}" "tenant create echoes Tenant scope"

e2e::log "spring secret delete --scope tenant ${tenant_secret}"
response="$(e2e::cli secret delete --scope tenant "${tenant_secret}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "tenant-scoped delete succeeds"

# --- --scope unit without --unit must fail loudly ---------------------------
e2e::log "spring secret list --scope unit (expect failure — no --unit)"
response="$(e2e::cli secret list --scope unit 2>&1 || true)"
code="${response##*$'\n'}"
if [[ "${code}" == "0" ]]; then
    e2e::fail "scope=unit without --unit should exit non-zero"
else
    e2e::ok "scope=unit without --unit exits non-zero"
fi

e2e::summary
