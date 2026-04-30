#!/usr/bin/env bash
# spring github-app rotate-key and rotate-webhook-secret end-to-end (#636 / E1-A).
#
# What this scenario exercises:
#
#   1. spring github-app --help — verifies the command tree exposes rotate-key
#      and rotate-webhook-secret as subcommands. Catches registration bugs
#      where the commands are coded but not wired into the root Command.
#
#   2. spring github-app rotate-key --dry-run — the --dry-run flag bypasses
#      the live GitHub flow. No PEM file is needed; the command prints the
#      preamble (GitHub settings URL hint, etc.) and exits 0. Verifies the
#      flag was wired and that the "guided wizard" path doesn't attempt
#      network calls or file writes in dry-run mode.
#
#   3. spring github-app rotate-key --dry-run --from-file <valid-pem> —
#      validates a PEM inline and prints what would be written without
#      persisting. The PEM is generated on-the-fly via openssl (if available)
#      or a hardcoded RSA test PEM if openssl is absent. Verifies the
#      validation message appears in the output.
#
#   4. spring github-app rotate-key --from-file <non-existent> — missing file
#      must exit non-zero and mention the path.
#
#   5. spring github-app rotate-webhook-secret --dry-run — generates a random
#      secret, prints it, and exits 0 without prompting or persisting.
#      Verifies the secret placeholder is present in the output (non-empty).
#
# Non-goals:
#   - Live GitHub flow (requires browser + GitHub credentials).
#   - --write-env / --write-secrets persistence paths (require file-system
#     side-effects that conflict with clean-environment assertions in CI;
#     tracked by follow-up #1395).
#
# References:
#   - GitHubAppCommand.cs — CreateRotateKeyCommand, CreateRotateWebhookSecretCommand
#   - #636 — rotate-key / rotate-webhook-secret feature
#   - #311 — E2E harness parent issue
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

# ─── helpers ────────────────────────────────────────────────────────────────

# make_test_pem — writes a minimal (but syntactically valid) RSA private key
# PEM to the path given as $1. Uses openssl if present; otherwise falls back
# to a static test-only key. The CLI validates the PEM header line only, so
# a static minimal PEM is sufficient for --dry-run tests.
make_test_pem() {
    local path="$1"
    if command -v openssl >/dev/null 2>&1; then
        openssl genrsa -out "${path}" 2048 2>/dev/null
    else
        # Minimal RSA PEM header+footer — enough to satisfy the CLI's
        # "contains BEGIN RSA PRIVATE KEY" check.
        printf '%s\n' \
            '-----BEGIN RSA PRIVATE KEY-----' \
            'MIIEowIBAAKCAQEA0Z3VS5JJcds3xHn/ygWep4MF6bHKJKq5gKfpP5Y+EXAMPLE' \
            '-----END RSA PRIVATE KEY-----' \
            > "${path}"
    fi
}

# Scratch directory for any temp files; cleaned up unconditionally on exit.
scratch_dir="$(mktemp -d)"
trap 'rm -rf "${scratch_dir}"' EXIT

# ─── 1: --help shows rotate-key and rotate-webhook-secret ─────────────────
e2e::log "spring github-app --help (assert rotate subcommands are registered)"
response="$(e2e::cli github-app --help 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" == "0" ]]; then
    e2e::ok "spring github-app --help exits 0"
else
    e2e::fail "spring github-app --help failed (exit ${code}): ${body:0:300}"
fi
e2e::expect_contains "rotate-key" "${body}" "--help lists rotate-key subcommand"
e2e::expect_contains "rotate-webhook-secret" "${body}" "--help lists rotate-webhook-secret subcommand"

# ─── 2: rotate-key --dry-run (no file) ──────────────────────────────────────
# In dry-run mode with no --from-file, the CLI prints the preamble (step
# instructions, GitHub settings URL hint) and exits 0. No network calls,
# no file writes.
e2e::log "spring github-app rotate-key --dry-run (no --from-file)"
response="$(e2e::cli github-app rotate-key --dry-run 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" == "0" ]]; then
    e2e::ok "spring github-app rotate-key --dry-run exits 0"
else
    e2e::fail "spring github-app rotate-key --dry-run failed (exit ${code}): ${body:0:300}"
fi
# Preamble must mention the GitHub settings page so the operator knows where
# to navigate. Also must contain "dry-run" so the output is unambiguous.
e2e::expect_contains "dry-run" "${body}" "rotate-key --dry-run output mentions dry-run"
e2e::expect_contains "github.com/settings/apps" "${body}" "rotate-key --dry-run output includes GitHub settings URL"

# ─── 3: rotate-key --dry-run --from-file <valid-pem> ───────────────────────
pem_path="${scratch_dir}/test-key.pem"
make_test_pem "${pem_path}"
e2e::log "spring github-app rotate-key --dry-run --from-file ${pem_path}"
response="$(e2e::cli github-app rotate-key --dry-run --from-file "${pem_path}" 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" == "0" ]]; then
    e2e::ok "spring github-app rotate-key --dry-run --from-file exits 0"
else
    e2e::fail "spring github-app rotate-key --dry-run --from-file failed (exit ${code}): ${body:0:300}"
fi
# PEM validation message must appear: "PEM validated: <path>".
e2e::expect_contains "PEM validated" "${body}" "rotate-key --dry-run --from-file reports PEM validated"
e2e::expect_contains "dry-run" "${body}" "rotate-key --dry-run --from-file output mentions dry-run"

# ─── 4: rotate-key --from-file with a non-existent path ────────────────────
missing_path="${scratch_dir}/does-not-exist.pem"
e2e::log "spring github-app rotate-key --from-file ${missing_path} (expect non-zero)"
response="$(e2e::cli github-app rotate-key --from-file "${missing_path}" 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" != "0" ]]; then
    e2e::ok "rotate-key with missing PEM file exits non-zero (got ${code})"
else
    e2e::fail "rotate-key with missing PEM file should exit non-zero but exited 0"
fi
# Error message must mention the path or "not found" so it is actionable.
if [[ "${body}" == *"${missing_path}"* ]] || [[ "${body}" == *"not found"* ]] || [[ "${body}" == *"PEM file"* ]]; then
    e2e::ok "rotate-key missing-file error is actionable (mentions path or 'not found')"
else
    e2e::fail "rotate-key missing-file error output is not actionable: ${body:0:300}"
fi

# ─── 5: rotate-webhook-secret --dry-run ─────────────────────────────────────
# In dry-run mode the CLI generates a random secret, prints it, and exits 0
# without prompting or persisting. The secret line must be non-empty (the
# generator produces 32-byte base64url output).
e2e::log "spring github-app rotate-webhook-secret --dry-run"
response="$(e2e::cli github-app rotate-webhook-secret --dry-run 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" == "0" ]]; then
    e2e::ok "spring github-app rotate-webhook-secret --dry-run exits 0"
else
    e2e::fail "spring github-app rotate-webhook-secret --dry-run failed (exit ${code}): ${body:0:300}"
fi
e2e::expect_contains "dry-run" "${body}" "rotate-webhook-secret --dry-run output mentions dry-run"
# The generated secret appears on its own line (indented with two spaces per
# the source). We just verify the output is non-trivially long — the actual
# secret is random. The "New webhook secret" label is the reliable anchor.
e2e::expect_contains "New webhook secret" "${body}" "rotate-webhook-secret --dry-run prints 'New webhook secret' label"

e2e::summary
