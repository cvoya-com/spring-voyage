#!/usr/bin/env bash
# pool: fast
# Sanity-check the spring CLI itself: it must start, parse, and print help
# without an API call. Catches regressions in CLI startup (missing dependency,
# broken DI, broken System.CommandLine wiring) before later scenarios spend
# real API time chasing what looks like a server error.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

e2e::log "spring --help"
response="$(e2e::cli --help)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

if [[ "${code}" == "0" ]]; then
    e2e::ok "spring --help exits 0 (exit ${code})"
else
    e2e::fail "spring --help — expected exit 0, got ${code}: ${body:0:500}"
fi

# Spot-check that the subcommand surface scenarios depend on is still there.
e2e::expect_contains "Spring Voyage CLI" "${body}" "help mentions the CLI description"
e2e::expect_contains "unit" "${body}" "help lists the 'unit' subcommand"
# `apply` was deleted by #1583 (213f39bc); the v0.1 install surface is
# `spring package install` (catalog install + status/retry/abort/export).
e2e::expect_contains "package" "${body}" "help lists the 'package' subcommand"

e2e::summary
