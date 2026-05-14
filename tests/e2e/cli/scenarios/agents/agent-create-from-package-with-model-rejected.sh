#!/usr/bin/env bash
# pool: fast
# `spring agent create --from-package --model` is rejected at parse time.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

export SPRING_API_URL="http://127.0.0.1:9"

response="$(e2e::cli_agent_create --name ada-conflict --from-package foo --model openai/gpt-4o 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

if [[ "${code}" != "0" ]]; then
    e2e::ok "from-package plus model exits non-zero (got ${code})"
else
    e2e::fail "from-package plus model should fail before HTTP"
fi

e2e::expect_contains \
    "error: --from-package is mutually exclusive with execution shorthands (--model)" \
    "${body}" \
    "from-package/model conflict message names both flags"

e2e::summary
