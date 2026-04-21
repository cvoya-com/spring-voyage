#!/usr/bin/env bash
# `spring unit policy <dim> get|set|clear` roundtrip (#453).
#
# Exercises the CLI-driven path for every UnitPolicy dimension that exists
# today — skill, model, cost, execution-mode, initiative — over the same
# single-endpoint surface that 15-unit-policy-roundtrip.sh already covers
# from raw HTTP. The goal here is to prove the per-dimension verbs correctly
# merge into / out of the unified policy envelope without clobbering the
# other slots.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

unit="$(e2e::unit_name policy-cli)"

trap 'e2e::cleanup_unit "${unit}"' EXIT

e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create succeeds"

# --- skill: set + get + clear round-trip (proves the merge) ------------------
e2e::log "spring unit policy skill set ${unit} --allowed github,filesystem --blocked shell"
response="$(e2e::cli --output json unit policy skill set "${unit}" --allowed github,filesystem --blocked shell)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "policy skill set succeeds"
e2e::expect_contains "\"github\"" "${body}" "skill set echoes allowed=github"
e2e::expect_contains "\"shell\"" "${body}" "skill set echoes blocked=shell"

# --- model: set on top of an existing skill policy, verify merge -------------
e2e::log "spring unit policy model set ${unit} --allowed gpt-4o-mini --blocked gpt-4o"
response="$(e2e::cli --output json unit policy model set "${unit}" --allowed gpt-4o-mini --blocked gpt-4o)"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "policy model set succeeds"

# Raw HTTP GET proves both dimensions survived — the per-dimension verb must
# never clobber the slots it didn't touch.
e2e::log "GET /api/v1/units/${unit}/policy (expect skill AND model present)"
response="$(e2e::http GET "/api/v1/units/${unit}/policy")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "GET /policy returns 200"
e2e::expect_contains "github" "${body}" "skill survives model set (merge)"
e2e::expect_contains "gpt-4o-mini" "${body}" "model now persisted"

# --- cost: numeric caps ------------------------------------------------------
e2e::log "spring unit policy cost set ${unit} --max-per-invocation 0.5 --max-per-hour 5 --max-per-day 25"
response="$(e2e::cli --output json unit policy cost set "${unit}" --max-per-invocation 0.5 --max-per-hour 5 --max-per-day 25)"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "policy cost set succeeds"

# --- execution-mode: pinned value --------------------------------------------
e2e::log "spring unit policy execution-mode set ${unit} --forced OnDemand"
response="$(e2e::cli --output json unit policy execution-mode set "${unit}" --forced OnDemand)"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "policy execution-mode set succeeds"

# --- initiative: blocked actions + max level ---------------------------------
e2e::log "spring unit policy initiative set ${unit} --max-level Proactive --blocked agent.spawn"
response="$(e2e::cli --output json unit policy initiative set "${unit}" --max-level Proactive --blocked agent.spawn)"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "policy initiative set succeeds"

# --- label-routing: trigger map + status-label roundtrip (#389) --------------
e2e::log "spring unit policy label-routing set ${unit} --trigger agent:backend=backend-engineer --add-on-assign in-progress --remove-on-assign agent:backend"
response="$(e2e::cli --output json unit policy label-routing set "${unit}" --trigger agent:backend=backend-engineer --add-on-assign in-progress --remove-on-assign agent:backend)"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "policy label-routing set succeeds"

# --- get each dimension returns the current slot + inheritance chain ---------
for dim in skill model cost execution-mode initiative label-routing; do
    e2e::log "spring unit policy ${dim} get ${unit}"
    response="$(e2e::cli --output json unit policy "${dim}" get "${unit}")"
    code="${response##*$'\n'}"
    body="${response%$'\n'*}"
    e2e::expect_status "0" "${code}" "policy ${dim} get succeeds"
    e2e::expect_contains "\"dimension\": \"${dim}\"" "${body}" "${dim} get echoes dimension"
    e2e::expect_contains "\"chain\"" "${body}" "${dim} get surfaces inheritance chain"
done

# --- clear each dimension and verify it comes back empty ---------------------
for dim in skill model cost execution-mode initiative label-routing; do
    e2e::log "spring unit policy ${dim} clear ${unit}"
    response="$(e2e::cli --output json unit policy "${dim}" clear "${unit}")"
    code="${response##*$'\n'}"
    e2e::expect_status "0" "${code}" "policy ${dim} clear succeeds"
done

e2e::log "GET /api/v1/units/${unit}/policy (expect empty after all-clear)"
response="$(e2e::http GET "/api/v1/units/${unit}/policy")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "GET /policy returns 200 after clear"
e2e::expect_contains "\"skill\":null" "${body}" "skill cleared"
e2e::expect_contains "\"model\":null" "${body}" "model cleared"
e2e::expect_contains "\"cost\":null" "${body}" "cost cleared"
e2e::expect_contains "\"executionMode\":null" "${body}" "executionMode cleared"
e2e::expect_contains "\"initiative\":null" "${body}" "initiative cleared"
e2e::expect_contains "\"labelRouting\":null" "${body}" "labelRouting cleared"

e2e::summary
