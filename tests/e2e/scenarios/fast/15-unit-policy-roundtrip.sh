#!/usr/bin/env bash
# Unit-policy CRUD roundtrip (#404).
#
# Exercises GET/PUT on /api/v1/units/{id}/policy for the two dimensions that
# have shipped: skill (#163) and model (#247). The fast path for policy
# enforcement-at-dispatch (the "blocks unauthorized skill use" / "rejects
# disallowed model" variants from #404) requires the agent's effective-model
# threading to match what the enforcer queries; that mismatch is a separate
# bug and is not in scope for this PR. The CRUD roundtrip alone covers the
# surface every operator tool (CLI, portal, apply) depends on.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

unit="$(e2e::unit_name policy-roundtrip)"

trap 'e2e::cleanup_unit "${unit}"' EXIT

# --- Setup -------------------------------------------------------------------
e2e::log "spring unit create ${unit}"
response="$(e2e::cli --output json unit create "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create succeeds"

# --- Initial GET returns UnitPolicy.Empty ------------------------------------
# Post-#162 a unit that has never had a policy persisted must return the
# canonical empty shape (all dimensions null) so callers never branch on
# 404 vs no-policy. Verify that here to guard the wire contract.
e2e::log "GET /api/v1/units/${unit}/policy (expect empty shape)"
response="$(e2e::http GET "/api/v1/units/${unit}/policy")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "GET /policy returns 200 even when none set"
e2e::expect_contains "\"skill\":null" "${body}" "empty policy exposes skill=null"
e2e::expect_contains "\"model\":null" "${body}" "empty policy exposes model=null"

# --- PUT a skill+model policy -------------------------------------------------
# Block one tool and whitelist one other; block one model and whitelist one.
# The response echoes the canonical post-write shape — assert it round-trips
# verbatim. This guards against the PUT handler silently dropping fields.
policy_body='{"skill":{"allowed":["send_message"],"blocked":["shell_exec"]},"model":{"allowed":["gpt-4o-mini"],"blocked":["gpt-4o"]}}'
e2e::log "PUT /api/v1/units/${unit}/policy (skill+model rules)"
response="$(e2e::http PUT "/api/v1/units/${unit}/policy" "${policy_body}")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "PUT /policy accepts the request"
e2e::expect_contains "\"send_message\"" "${body}" "PUT response carries the allowed skill"
e2e::expect_contains "\"shell_exec\"" "${body}" "PUT response carries the blocked skill"
e2e::expect_contains "\"gpt-4o-mini\"" "${body}" "PUT response carries the allowed model"
e2e::expect_contains "\"gpt-4o\"" "${body}" "PUT response carries the blocked model"

# --- GET must now return the persisted policy --------------------------------
# Re-read proves the PUT actually persisted (not just echoed the request).
e2e::log "GET /api/v1/units/${unit}/policy (expect the PUT body to round-trip)"
response="$(e2e::http GET "/api/v1/units/${unit}/policy")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "GET /policy returns 200 after PUT"
e2e::expect_contains "\"send_message\"" "${body}" "GET round-trips the allowed skill"
e2e::expect_contains "\"shell_exec\"" "${body}" "GET round-trips the blocked skill"
e2e::expect_contains "\"gpt-4o-mini\"" "${body}" "GET round-trips the allowed model"
e2e::expect_contains "\"gpt-4o\"" "${body}" "GET round-trips the blocked model"

# --- PUT an empty policy clears the stored values ----------------------------
# The canonical "clear" shape is { skill: null, model: null, ... } — verify
# a subsequent GET reflects the cleared state so operators can reset a
# unit's policy without deleting the unit.
e2e::log "PUT /api/v1/units/${unit}/policy (clear)"
response="$(e2e::http PUT "/api/v1/units/${unit}/policy" '{}')"
status="${response##*$'\n'}"
e2e::expect_status "200" "${status}" "PUT /policy accepts an empty body"

response="$(e2e::http GET "/api/v1/units/${unit}/policy")"
status="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "200" "${status}" "GET /policy returns 200 after clear"
e2e::expect_contains "\"skill\":null" "${body}" "cleared policy exposes skill=null"
e2e::expect_contains "\"model\":null" "${body}" "cleared policy exposes model=null"

# --- Unknown unit returns 404 ------------------------------------------------
# Contract guard: GET on a non-existent unit must 404 (not 200 with empty),
# so callers can tell "no such unit" apart from "exists but no policy".
missing_unit="$(e2e::unit_name policy-missing-$(date +%s%N))"
e2e::log "GET /api/v1/units/${missing_unit}/policy (expect 404)"
response="$(e2e::http GET "/api/v1/units/${missing_unit}/policy")"
status="${response##*$'\n'}"
e2e::expect_status "404" "${status}" "GET /policy on unknown unit returns 404"

e2e::summary
