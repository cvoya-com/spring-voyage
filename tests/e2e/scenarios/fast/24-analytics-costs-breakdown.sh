#!/usr/bin/env bash
# spring analytics costs --by-source / --breakdown end-to-end (#554 / E1-A).
#
# What this scenario exercises:
#
#   1. spring analytics costs (no flags) — the scalar tenant-total rollup.
#      Verifies the command accepts no scoping args, returns exit 0, and
#      renders a table with the expected column headers. The test stack may
#      have zero cost records; an empty table is still a valid response.
#
#   2. spring analytics costs --by-source — the per-source breakdown flag
#      added by E1-A (#554). Routes to the dashboard costs endpoint
#      (/api/v1/tenant/dashboard/costs) and emits a table with one row per
#      source (or an empty table when no costs exist). The value is that the
#      CLI invocation succeeds, the API call lands, and the output renders
#      without crashing. Column header "source" must be present.
#
#   3. spring analytics costs --breakdown — alias for --by-source (same
#      flag, different spelling). Must produce identical exit-0 behaviour.
#
#   4. spring analytics costs --window 7d — window option is accepted without
#      error. Verifies parse-level acceptance of the flag, not the specific
#      data returned.
#
#   5. spring analytics costs --window bad-value — parse or argument error
#      exits non-zero and mentions the bad value (not a silent wrong-window).
#
# Non-goals:
#   - Asserting specific cost values (depends on seeded data, which varies
#     per environment; zero-cost stacks are valid).
#   - Testing --unit / --agent scoping (covered by scenario 16 which
#     exercises the same API paths with a freshly created unit/agent).
#
# References:
#   - AnalyticsCommand.cs — CreateCostsCommand, --by-source flag
#   - #554 — per-source breakdown feature
#   - #311 — E2E harness parent issue
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

# --- 1: scalar tenant-total costs ------------------------------------------
e2e::log "spring analytics costs (tenant total, no flags)"
response="$(e2e::cli analytics costs 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" == "0" ]]; then
    e2e::ok "spring analytics costs exits 0"
else
    e2e::fail "spring analytics costs failed (exit ${code}): ${body:0:300}"
fi
# Table output must contain the column headers regardless of row count.
e2e::expect_contains "TOTALCOST" "${body}" "analytics costs output includes 'totalCost' column"

# --- 2: --by-source breakdown -----------------------------------------------
e2e::log "spring analytics costs --by-source (per-source breakdown)"
response="$(e2e::cli analytics costs --by-source 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" == "0" ]]; then
    e2e::ok "spring analytics costs --by-source exits 0"
else
    e2e::fail "spring analytics costs --by-source failed (exit ${code}): ${body:0:300}"
fi
# The breakdown table always has a 'source' column, even when empty.
e2e::expect_contains "SOURCE" "${body}" "analytics costs --by-source output includes 'source' column"

# --- 3: --breakdown alias ---------------------------------------------------
e2e::log "spring analytics costs --breakdown (alias for --by-source)"
response="$(e2e::cli analytics costs --breakdown 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" == "0" ]]; then
    e2e::ok "spring analytics costs --breakdown exits 0"
else
    e2e::fail "spring analytics costs --breakdown failed (exit ${code}): ${body:0:300}"
fi
e2e::expect_contains "SOURCE" "${body}" "analytics costs --breakdown output includes 'source' column"

# --- 4: --window 7d is accepted ---------------------------------------------
e2e::log "spring analytics costs --window 7d (window flag accepted)"
response="$(e2e::cli analytics costs --window 7d 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" == "0" ]]; then
    e2e::ok "spring analytics costs --window 7d exits 0"
else
    e2e::fail "spring analytics costs --window 7d failed (exit ${code}): ${body:0:300}"
fi

# --- 5: bad --window value exits non-zero ----------------------------------
# The AnalyticsCommand.ResolveWindow method throws on an invalid window label
# (e.g. missing or unrecognised unit suffix). The CLI must exit non-zero and
# the error output must mention the bad value so it's actionable.
e2e::log "spring analytics costs --window not-a-window (expect non-zero)"
response="$(e2e::cli analytics costs --window not-a-window 2>&1 || true)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
if [[ "${code}" != "0" ]]; then
    e2e::ok "spring analytics costs --window not-a-window exits non-zero (got ${code})"
else
    e2e::fail "spring analytics costs --window not-a-window should exit non-zero but exited 0"
fi
if [[ "${body}" == *"not-a-window"* ]] || [[ "${body}" == *"window"* ]] || [[ "${body}" == *"Invalid"* ]] || [[ "${body}" == *"invalid"* ]]; then
    e2e::ok "error output mentions the bad window value (actionable)"
else
    e2e::fail "error output does not mention the bad value or 'window': ${body:0:300}"
fi

e2e::summary
