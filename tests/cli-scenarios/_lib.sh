# shellcheck shell=bash
# Shared helpers for tests/cli-scenarios. Source me.

set -o pipefail

: "${E2E_BASE_URL:=http://localhost}"
: "${E2E_CURL_OPTS:=--silent --show-error --max-time 30}"

# Repo root (two levels up from tests/cli-scenarios). Scenarios may override.
: "${E2E_REPO_ROOT:=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

# Run-identity envelope. `run.sh` generates a single E2E_RUN_ID at its top and
# exports it so every scenario in the batch derives unit/agent names from the
# same id. When a scenario is invoked standalone (no run.sh wrapper), the
# default below produces a fresh id so names are still unique per invocation.
#
# E2E_PREFIX is the static leading segment so orphan sweeps can find every
# artefact a previous run left behind. CI deployments typically override it
# (e.g. E2E_PREFIX=e2e-ci) to carve their namespace out of the default.
: "${E2E_PREFIX:=e2e}"
: "${E2E_RUN_ID:=$(date +%s)-$$}"
export E2E_PREFIX E2E_RUN_ID

# e2e::unit_name SUFFIX — deterministic unit name for the current run.
# Format: "${E2E_PREFIX}-${E2E_RUN_ID}-<suffix>". Two concurrent runs get
# different run ids, so names never collide.
e2e::unit_name() {
    printf '%s-%s-%s' "${E2E_PREFIX}" "${E2E_RUN_ID}" "$1"
}

# e2e::agent_name SUFFIX — same convention as e2e::unit_name, but for agents.
# Kept as a separate helper so scenarios that create both don't accidentally
# share the same identifier.
e2e::agent_name() {
    printf '%s-%s-%s' "${E2E_PREFIX}" "${E2E_RUN_ID}" "$1"
}

# CLI invocation. Override SPRING_CLI to point at a prebuilt binary, e.g.
#   SPRING_CLI=/usr/local/bin/spring ./run.sh
# Default uses `dotnet run` against the in-repo CLI project so users don't need
# a build step. The `--` after the project path separates dotnet-run args from
# the args forwarded to the CLI itself.
: "${SPRING_CLI:=dotnet run --project ${E2E_REPO_ROOT}/src/Cvoya.Spring.Cli --}"

# SPRING_API_URL is read by `spring apply` (and exported here so any future
# CLI commands that read the same env var Just Work). Other CLI commands fall
# back to ~/.spring/config.json — see src/Cvoya.Spring.Cli/CliConfig.cs.
# When the API runs with LocalDev=true, no token is required.
if [[ -n "${SPRING_API_URL:-}" ]]; then export SPRING_API_URL; fi

_e2e_pass=0
_e2e_fail=0
_e2e_failures=()

e2e::log() { printf '[e2e] %s\n' "$*" >&2; }
e2e::ok()  { printf '[e2e][PASS] %s\n' "$*" >&2; _e2e_pass=$((_e2e_pass+1)); }
e2e::fail() { printf '[e2e][FAIL] %s\n' "$*" >&2; _e2e_fail=$((_e2e_fail+1)); _e2e_failures+=("$*"); }

# e2e::http METHOD PATH [BODY-JSON] — prints "<body>\n<status>"
e2e::http() {
    local method="$1" path="$2" body="${3:-}"
    local url="${E2E_BASE_URL}${path}"
    if [[ -n "${body}" ]]; then
        # shellcheck disable=SC2086
        curl ${E2E_CURL_OPTS} -w '\n%{http_code}' -X "${method}" -H 'Content-Type: application/json' -d "${body}" "${url}"
    else
        # shellcheck disable=SC2086
        curl ${E2E_CURL_OPTS} -w '\n%{http_code}' -X "${method}" "${url}"
    fi
}

# e2e::cli ARGS... — runs the spring CLI with the given args, prints
# "<combined stdout+stderr>\n<exit-code>". Mirrors the e2e::http output shape
# so scenarios can split with the same `${response##*$'\n'}` pattern.
#
# stdout and stderr are merged because `dotnet run` writes build banners to
# stderr and assertions need to see the full picture without losing exit code.
e2e::cli() {
    local out code
    # SPRING_CLI is intentionally word-split: it may be a single binary path
    # ("/usr/local/bin/spring") or a multi-token command
    # ("dotnet run --project … --").
    # shellcheck disable=SC2086
    out="$(${SPRING_CLI} "$@" 2>&1)"
    code=$?
    printf '%s\n%d' "${out}" "${code}"
}

# _e2e_split_root_args — partitions a flat arg list into "root-level
# options that must appear BEFORE the subcommand" and "everything else".
# The CLI's System.CommandLine setup declares --output / -o on the root
# command; options bound to the root do NOT propagate to subcommand
# positions reliably, so helpers that re-assemble a command line must
# hoist them to the front (`spring --output json unit create <name>`,
# NOT `spring unit create <name> --output json`, which prints help).
#
# Emits two arrays on stdout as NUL-separated records so the caller can
# read them back into real arrays without word-splitting surprises.
#
# Usage: mapfile -t root_args rest_args < <(_e2e_split_root_args "$@")
# is awkward; instead the helpers below just rebuild both arrays inline.
#
# The predicate list is intentionally narrow — only options the scenarios
# actually pass today. Add more here if new root options appear.
_e2e_is_root_option() {
    case "$1" in
        --output|-o|--output=*|-o=*) return 0 ;;
        *) return 1 ;;
    esac
}

# e2e::cli_unit_create ARGS... — wraps `spring unit create` and injects
# --top-level when the caller hasn't already supplied either --top-level or
# --parent-unit. Mirrors the CLI's #744 contract: every unit needs exactly
# one of those two flags. Keeps scenarios free of parent-bookkeeping noise
# when they just want a standalone unit, and lets the nested-units scenario
# opt in to --parent-unit explicitly without the helper second-guessing it.
#
# Root-level options (`--output` / `-o`) embedded in ARGS are hoisted to
# before the `unit create` subcommand — the System.CommandLine parser does
# not accept them after the subcommand name (see _e2e_is_root_option).
# Exit code / stdout shape matches e2e::cli so callers can keep the
# `"${response##*$'\n'}"` split pattern.
e2e::cli_unit_create() {
    local has_parent=0 has_top=0
    local -a root_args=() sub_args=()
    local i=1 arg
    while (( i <= $# )); do
        arg="${!i}"
        if _e2e_is_root_option "${arg}"; then
            root_args+=("${arg}")
            # `--output json` takes a value; `--output=json` does not.
            if [[ "${arg}" == "--output" || "${arg}" == "-o" ]]; then
                i=$((i+1))
                if (( i <= $# )); then root_args+=("${!i}"); fi
            fi
        else
            sub_args+=("${arg}")
            case "${arg}" in
                --parent-unit|--parent-unit=*) has_parent=1 ;;
                --top-level) has_top=1 ;;
            esac
        fi
        i=$((i+1))
    done
    if (( has_parent == 0 && has_top == 0 )); then
        sub_args+=(--top-level)
    fi
    e2e::cli "${root_args[@]}" unit create "${sub_args[@]}"
}

# e2e::cli_agent_create ARGS... — wraps `spring agent create`. The #744
# contract requires ≥1 `--unit <id>` flag; this helper does no injection
# (there is no sensible default) but exists so scenario call sites go
# through a single name, making future drift trivial to fix in one place.
# Same root-option hoisting as the unit helpers.
e2e::cli_agent_create() {
    local -a root_args=() sub_args=()
    local i=1 arg
    while (( i <= $# )); do
        arg="${!i}"
        if _e2e_is_root_option "${arg}"; then
            root_args+=("${arg}")
            if [[ "${arg}" == "--output" || "${arg}" == "-o" ]]; then
                i=$((i+1))
                if (( i <= $# )); then root_args+=("${!i}"); fi
            fi
        else
            sub_args+=("${arg}")
        fi
        i=$((i+1))
    done
    e2e::cli "${root_args[@]}" agent create "${sub_args[@]}"
}

e2e::expect_status() {
    local expected="$1" actual="$2" desc="$3"
    if [[ "${actual}" == "${expected}" ]]; then e2e::ok "${desc} (status ${actual})"; else e2e::fail "${desc} — expected ${expected}, got ${actual}"; fi
}

e2e::expect_contains() {
    local needle="$1" haystack="$2" desc="$3"
    if [[ "${haystack}" == *"${needle}"* ]]; then e2e::ok "${desc}"; else e2e::fail "${desc} — did not find '${needle}' in: ${haystack:0:500}"; fi
}

# e2e::cleanup_unit NAME [NAME...] — cascading teardown for one or more units.
# Runs `spring unit purge --confirm <name>` per unit, swallows failures, and
# logs the outcome. Intended for use in scenario EXIT traps:
#
#   trap 'e2e::cleanup_unit "${name}"' EXIT
#
# Cleanup never masks a scenario's real exit code: every purge is best-effort
# and errors are reported via e2e::log (not e2e::fail). The helper preserves
# the caller's pending `$?` by capturing it on entry and returning it at the
# end — that way, when this helper is the last command in an EXIT trap (as in
# every scenario's `trap '…e2e::cleanup_unit…' EXIT`), bash does NOT overwrite
# the script's pending exit code with 0 from the cleanup's last CLI call. See
# #1030: without this, a scenario that fails assertions but cleans up happily
# exits 0 and run.sh marks it passed.
#
# --confirm gates the destructive op as the CLI requires.
e2e::cleanup_unit() {
    local rc=$?
    local unit
    for unit in "$@"; do
        [[ -z "${unit}" ]] && continue
        if e2e::cli unit purge "${unit}" --confirm >/dev/null 2>&1; then
            e2e::log "cleanup: purged unit ${unit}"
        else
            e2e::log "cleanup: purge failed for ${unit} (ignored)"
        fi
    done
    return "${rc}"
}

# e2e::cleanup_agent NAME [NAME...] — companion to cleanup_unit for agents
# created outside any unit (e.g. the nested-units scenario doesn't need this,
# but 06-unit-membership-roundtrip creates an agent that is removed after the
# unit purge cascades). Same swallow-and-log contract — including the #1030
# fix: preserves the caller's pending `$?` so chained trap invocations like
# `trap '…cleanup_unit …; cleanup_agent …' EXIT` don't mask a failing summary.
e2e::cleanup_agent() {
    local rc=$?
    local agent
    for agent in "$@"; do
        [[ -z "${agent}" ]] && continue
        if e2e::cli agent purge "${agent}" --confirm >/dev/null 2>&1; then
            e2e::log "cleanup: purged agent ${agent}"
        else
            e2e::log "cleanup: purge failed for ${agent} (ignored)"
        fi
    done
    return "${rc}"
}

# e2e::require_ollama — pings the configured local LLM endpoint. Returns 0 when
# Ollama is reachable, 1 otherwise. Scenarios that need an LLM call this first
# and skip (not fail) when it's down, so the base scenario set stays green on
# hosts without Ollama configured.
#
# Reads LLM_BASE_URL (takes precedence), then LanguageModel__Ollama__BaseUrl,
# then falls back to http://localhost:11434.
e2e::require_ollama() {
    local url="${LLM_BASE_URL:-${LanguageModel__Ollama__BaseUrl:-http://localhost:11434}}"
    url="${url%/}"
    # shellcheck disable=SC2086
    if curl ${E2E_CURL_OPTS} --output /dev/null --fail "${url}/api/tags" 2>/dev/null; then
        return 0
    fi
    e2e::log "Ollama not reachable at ${url}/api/tags — skipping LLM scenario."
    e2e::log "  See docs/developer/local-ai-ollama.md to enable the local LLM backend."
    return 1
}

e2e::summary() {
    printf '\n[e2e] %d passed, %d failed\n' "${_e2e_pass}" "${_e2e_fail}"
    if (( _e2e_fail > 0 )); then
        printf '[e2e] Failures:\n'
        for f in "${_e2e_failures[@]}"; do printf '  - %s\n' "$f"; done
        return 1
    fi
    return 0
}
