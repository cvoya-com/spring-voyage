#!/usr/bin/env bash
# pool: fast
# `spring engagement list` + `spring thread show` round-trip.
# Sends a Domain message to an agent (no LLM; ephemeral one-shot dispatch
# is a valid event source even without a real reply) and asserts the
# resulting engagement is listed by `engagement list` and addressable via
# `thread show`. This proves the engagement API surface is wired without
# requiring a full LLM round-trip.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

unit="$(e2e::unit_name engmnt)"
agent="$(e2e::agent_name engmnt)"
thread_id="e2e-engmnt-$(date +%s)-$$"

cleanup() {
    e2e::cleanup_unit "${unit}"
    e2e::cleanup_agent "${agent}"
}
trap cleanup EXIT

# --- Setup ------------------------------------------------------------------
e2e::log "spring unit create ${unit}"
response="$(e2e::cli_unit_create --output json "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "unit create succeeds"

e2e::log "spring agent create ${agent} --unit ${unit}"
response="$(e2e::cli_agent_create --output json "${agent}" --unit "${unit}")"
code="${response##*$'\n'}"
e2e::expect_status "0" "${code}" "agent create succeeds"

# --- Kick off an engagement by sending a Domain message ---------------------
e2e::log "spring message send agent://${agent} '...' --thread ${thread_id}"
response="$(e2e::cli --output json message send "agent://${agent}" "Hello, just creating an engagement." --thread "${thread_id}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "message send succeeds"
e2e::expect_contains "messageId" "${body}" "send response carries a messageId"

# --- engagement list scoped to the agent ------------------------------------
# The engagement index is eventually consistent w.r.t. the message
# accept/route path, so a fresh thread can take a moment to surface in
# the list. Poll up to ~10s before asserting.
deadline=$(( $(date +%s) + 10 ))
last_body=""
last_code=""
while (( $(date +%s) < deadline )); do
    response="$(e2e::cli --output json engagement list --agent "${agent}")"
    last_code="${response##*$'\n'}"
    last_body="${response%$'\n'*}"
    if [[ "${last_code}" == "0" ]] && printf '%s' "${last_body}" | grep -q "${thread_id}"; then
        break
    fi
    sleep 1
done
e2e::log "spring engagement list --agent ${agent} --output json (after polling)"
e2e::expect_status "0" "${last_code}" "engagement list --agent succeeds"
e2e::expect_contains "${thread_id}" "${last_body}" "engagement list mentions the new thread id"

# --- thread show by id -------------------------------------------------------
e2e::log "spring thread show ${thread_id} --output json"
response="$(e2e::cli --output json thread show "${thread_id}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "thread show succeeds"
e2e::expect_contains "MessageReceived" "${body}" "thread show carries the MessageReceived event"
e2e::expect_contains "${thread_id}" "${body}" "thread show body references the thread id"

# --- engagement list scoped to the unit -------------------------------------
e2e::log "spring engagement list --unit ${unit} --output json"
response="$(e2e::cli --output json engagement list --unit "${unit}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "engagement list --unit succeeds"
# A unit-scoped engagement may or may not include this thread depending on
# how the message was routed (Domain message to agent does not always
# create a unit engagement). Only the smoke status is asserted; agreement
# with --agent is exercised separately by the LLM-pool flow.

e2e::summary
