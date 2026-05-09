#!/usr/bin/env bash
# pool: fast
# ADR-0039 §8 / §9: `spring agent create` no longer accepts a positional
# name/id token. The CLI rejects it at parse time with a migration hint.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

# Use an unroutable API endpoint so the assertion proves the command failed
# before any HTTP client was needed.
export SPRING_API_URL="http://127.0.0.1:9"

e2e::log "spring agent create --name my-agent my-positional-token (removed positional)"
response="$(e2e::cli agent create --name my-agent my-positional-token)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

# Parse-time rejection must surface a non-zero exit code. System.CommandLine
# returns exit 1 for argument-validator errors.
if [[ "${code}" != "0" ]]; then
    e2e::ok "positional agent-create token rejected at parse time (exit ${code})"
else
    e2e::fail "positional agent-create token accepted (exit ${code}); expected non-zero. Body: ${body:0:500}"
fi

e2e::expect_contains \
    "Positional <name> was removed in ADR-0039. Use --name <display-name> to set the agent's display name." \
    "${body}" \
    "rejection message prints the ADR-0039 migration hint"

e2e::summary
