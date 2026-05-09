#!/usr/bin/env bash
# pool: fast
# `spring agent create --input` without `--from-package` is rejected at parse
# time.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

export SPRING_API_URL="http://127.0.0.1:9"

response="$(e2e::cli_agent_create --name ada-conflict --input k=v 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

if [[ "${code}" != "0" ]]; then
    e2e::ok "input without from-package exits non-zero (got ${code})"
else
    e2e::fail "input without from-package should fail before HTTP"
fi

e2e::expect_contains \
    "error: --input requires --from-package" \
    "${body}" \
    "input error names --input and --from-package"

e2e::summary
