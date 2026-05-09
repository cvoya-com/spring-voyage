#!/usr/bin/env bash
# pool: fast
# `spring agent create --inherit --definition` is rejected at parse time.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

export SPRING_API_URL="http://127.0.0.1:9"

response="$(e2e::cli_agent_create --name ada-conflict --inherit --definition '{"execution":{"runtime":"codex"}}' 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

if [[ "${code}" != "0" ]]; then
    e2e::ok "inherit plus definition exits non-zero (got ${code})"
else
    e2e::fail "inherit plus definition should fail before HTTP"
fi

e2e::expect_contains \
    "error: --inherit is mutually exclusive with --definition and --definition-file" \
    "${body}" \
    "inherit/definition conflict message names both flags"

e2e::summary
