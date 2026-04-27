#!/usr/bin/env bash
# Cost-query API shape (#404).
#
# The cost pipeline aggregates CostRecord rows per-agent / per-unit / per-
# tenant and exposes them via /api/v1/costs/*. Fast-pool scenarios cannot
# generate real cost events (those are emitted by a live LLM turn), so this
# scenario instead asserts the QUERY side of the pipeline: a brand-new unit
# and agent report zero cost, and the tenant aggregate returns a well-formed
# CostSummary payload. When #330 lights up the LLM backend, the llm-pool
# companion will drive a real turn and assert non-zero aggregates here.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

unit="$(e2e::unit_name cost-query)"
agent="$(e2e::agent_name cost-query)"

trap 'e2e::cleanup_unit "${unit}"; e2e::cleanup_agent "${agent}"' EXIT

# --- Setup -------------------------------------------------------------------
e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create succeeds"

e2e::log "spring agent create ${agent} --unit ${unit}"
response="$(e2e::cli_agent_create --output json "${agent}" --unit "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "agent create succeeds"

# --- Per-agent cost: a fresh agent has no records ---------------------------
# The response schema (CostSummaryResponse) is required to carry the full
# field set even when nothing has been spent; a missing field here would
# break every dashboard client. Assert the zero-state shape explicitly.
e2e::log "GET /api/v1/costs/agents/${agent}"
response="$(e2e::http GET "/api/v1/tenant/cost/agents/${agent}")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "cost query for new agent returns 200"
e2e::expect_contains "\"totalCost\":0" "${body}" "fresh agent: totalCost is zero"
e2e::expect_contains "\"totalInputTokens\":0" "${body}" "fresh agent: totalInputTokens is zero"
e2e::expect_contains "\"totalOutputTokens\":0" "${body}" "fresh agent: totalOutputTokens is zero"
e2e::expect_contains "\"recordCount\":0" "${body}" "fresh agent: recordCount is zero"
e2e::expect_contains "\"workCost\":0" "${body}" "fresh agent: workCost is zero"
e2e::expect_contains "\"initiativeCost\":0" "${body}" "fresh agent: initiativeCost is zero"
e2e::expect_contains "\"from\":" "${body}" "response includes the aggregation window start"
e2e::expect_contains "\"to\":" "${body}" "response includes the aggregation window end"

# --- Per-unit cost: a fresh unit has no records -----------------------------
e2e::log "GET /api/v1/costs/units/${unit}"
response="$(e2e::http GET "/api/v1/tenant/cost/units/${unit}")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "cost query for new unit returns 200"
e2e::expect_contains "\"totalCost\":0" "${body}" "fresh unit: totalCost is zero"
e2e::expect_contains "\"recordCount\":0" "${body}" "fresh unit: recordCount is zero"

# --- Tenant aggregate: always returns a well-formed payload -----------------
# The default tenant id is supplied by the server; callers just hit the
# bare tenant endpoint. The value may be non-zero on a shared environment
# (previous runs' residue), so we only assert the shape here.
e2e::log "GET /api/v1/costs/tenant"
response="$(e2e::http GET "/api/v1/tenant/cost/tenant")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "tenant cost query returns 200"
e2e::expect_contains "\"totalCost\":" "${body}" "tenant payload carries totalCost"
e2e::expect_contains "\"recordCount\":" "${body}" "tenant payload carries recordCount"
e2e::expect_contains "\"from\":" "${body}" "tenant payload carries window start"
e2e::expect_contains "\"to\":" "${body}" "tenant payload carries window end"

# --- Time-range override is respected ---------------------------------------
# Passing a from/to window outside the current instant must still return
# 200 with zero counts; a 400 here would indicate the query-param parsing
# regressed.
past_from="2020-01-01T00:00:00Z"
past_to="2020-01-02T00:00:00Z"
e2e::log "GET /api/v1/costs/agents/${agent}?from=${past_from}&to=${past_to}"
response="$(e2e::http GET "/api/v1/tenant/cost/agents/${agent}?from=${past_from}&to=${past_to}")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "cost query with explicit from/to returns 200"
e2e::expect_contains "\"totalCost\":0" "${body}" "past-window agent cost is zero"

e2e::summary
