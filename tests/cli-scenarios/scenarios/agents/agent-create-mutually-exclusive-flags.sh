#!/usr/bin/env bash
# pool: fast
# `spring agent create` rejects mutually exclusive create-mode flags at parse time.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

# Use an unroutable API endpoint so the assertions prove the command failed
# before any HTTP client was needed.
export SPRING_API_URL="http://127.0.0.1:9"

e2e::log "spring agent create --name ada-conflict --inherit --runtime claude-code (expect parse-time error)"
response="$(e2e::cli_agent_create --name ada-conflict --inherit --runtime claude-code 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" != "0" ]]; then
    e2e::ok "inherit plus runtime exits non-zero (got ${code})"
else
    e2e::fail "inherit plus runtime should fail before HTTP"
fi
e2e::expect_contains \
    "error: --inherit is mutually exclusive with execution shorthands" \
    "${body}" \
    "inherit/runtime conflict message names the conflicting flag family"

e2e::log "spring agent create --name ada-conflict --from-package my-package --model claude-opus-4-7 (expect parse-time error)"
response="$(e2e::cli_agent_create --name ada-conflict --from-package my-package --model claude-opus-4-7 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" != "0" ]]; then
    e2e::ok "from-package plus model exits non-zero (got ${code})"
else
    e2e::fail "from-package plus model should fail before HTTP"
fi
e2e::expect_contains \
    "error: --from-package is mutually exclusive with execution shorthands (--image, --runtime, --model-provider, --model)" \
    "${body}" \
    "from-package/model conflict message names the conflicting flags"

e2e::summary
