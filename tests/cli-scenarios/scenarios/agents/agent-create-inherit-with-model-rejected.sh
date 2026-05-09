#!/usr/bin/env bash
# pool: fast
# `spring agent create --inherit --model` is rejected at parse time.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

export SPRING_API_URL="http://127.0.0.1:9"

response="$(e2e::cli_agent_create --name ada-conflict --inherit --model openai/gpt-4o 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

if [[ "${code}" != "0" ]]; then
    e2e::ok "inherit plus model exits non-zero (got ${code})"
else
    e2e::fail "inherit plus model should fail before HTTP"
fi

e2e::expect_contains \
    "error: --inherit is mutually exclusive with execution shorthands (--model)" \
    "${body}" \
    "inherit/model conflict message names both flags"

e2e::summary
