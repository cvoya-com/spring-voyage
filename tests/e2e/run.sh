#!/usr/bin/env bash
# Master runner for tests/e2e/scenarios.
#
# Scenarios are split into two pools:
#   scenarios/fast/  — no LLM. Exercises the running stack via Web API + CLI.
#                      Default for every invocation.
#   scenarios/llm/   — same surface as fast, plus a live LLM backend reachable
#                      via Ollama (LLM_BASE_URL). Opt-in with --llm.
#
# Usage:
#   ./run.sh                     run every scenarios/fast/*.sh (default)
#   ./run.sh --llm               run every scenarios/llm/*.sh (requires Ollama)
#   ./run.sh --all               run fast + llm pools, fast first
#   ./run.sh '12-*'              glob across all pools (no pool filter)
#   ./run.sh --sweep             delete every unit/agent whose name starts with
#                                "${E2E_PREFIX}-" (default prefix: "e2e")
#
# Environment:
#   E2E_PREFIX     Static leading segment of every generated name. CI overrides
#                  to carve out its own namespace (e.g. E2E_PREFIX=e2e-ci).
#   E2E_RUN_ID     Unique-per-run suffix appended to E2E_PREFIX. Generated here
#                  once and exported so every scenario sees the same id.
#   LLM_BASE_URL   Required for --llm mode (see #330).
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Generate one run id for the entire batch and export it. Scenarios source
# _lib.sh, which falls back to its own default only when this variable is
# absent (i.e. when the scenario runs standalone).
: "${E2E_PREFIX:=e2e}"
: "${E2E_RUN_ID:=$(date +%s)-$$}"
export E2E_PREFIX E2E_RUN_ID

# --sweep runs the orphan-cleanup path and exits. It's deliberately NOT part of
# a normal ./run.sh invocation: sweeping between scenarios would delete other
# concurrent runs' in-flight units. Callers must opt in.
if [[ "${1:-}" == "--sweep" ]]; then
    # shellcheck disable=SC1091
    source "${HERE}/_lib.sh"
    e2e::log "sweep: deleting every unit/agent whose name starts with '${E2E_PREFIX}-'"

    deleted_units=0
    deleted_agents=0
    failed=0

    # Units. `spring unit list --output json` returns a JSON array of
    # { id, name, ... } where `id` is the internal ActorId (a GUID) and
    # `name` is the address path. Both `spring unit delete` and the
    # underlying DELETE /api/v1/units/{id} endpoint resolve the target by
    # address path (see UnitEndpoints.DeleteUnitAsync: `Address("unit", id)`),
    # so we filter by name and pass the name through.
    list_response="$(e2e::cli --output json unit list)"
    list_code="${list_response##*$'\n'}"
    list_body="${list_response%$'\n'*}"
    if [[ "${list_code}" != "0" ]]; then
        e2e::log "sweep: unit list failed (exit ${list_code}): ${list_body:0:500}"
        failed=$((failed+1))
    else
        while IFS= read -r name; do
            [[ -z "${name}" ]] && continue
            e2e::log "sweep: deleting unit ${name}"
            if e2e::cli unit delete "${name}" > /dev/null; then
                deleted_units=$((deleted_units+1))
            else
                e2e::log "sweep: failed to delete unit ${name}"
                failed=$((failed+1))
            fi
        done < <(printf '%s' "${list_body}" | jq -r --arg p "${E2E_PREFIX}-" '.[] | select(.name | startswith($p)) | .name')
    fi

    # Agents. Same contract: list exposes { id: ActorId, name: path },
    # DELETE resolves by path (AgentEndpoints.DeleteAgentAsync:
    # `Address("agent", id)`), so filter and delete by name.
    list_response="$(e2e::cli --output json agent list)"
    list_code="${list_response##*$'\n'}"
    list_body="${list_response%$'\n'*}"
    if [[ "${list_code}" != "0" ]]; then
        e2e::log "sweep: agent list failed (exit ${list_code}): ${list_body:0:500}"
        failed=$((failed+1))
    else
        while IFS= read -r name; do
            [[ -z "${name}" ]] && continue
            e2e::log "sweep: deleting agent ${name}"
            if e2e::cli agent delete "${name}" > /dev/null; then
                deleted_agents=$((deleted_agents+1))
            else
                e2e::log "sweep: failed to delete agent ${name}"
                failed=$((failed+1))
            fi
        done < <(printf '%s' "${list_body}" | jq -r --arg p "${E2E_PREFIX}-" '.[] | select(.name | startswith($p)) | .name')
    fi

    printf '\n===== SWEEP SUMMARY =====\n'
    printf 'Prefix: %s-\n' "${E2E_PREFIX}"
    printf 'Deleted %d unit(s), %d agent(s)\n' "${deleted_units}" "${deleted_agents}"
    if (( failed > 0 )); then
        printf '%d delete(s) failed — see log above\n' "${failed}"
        exit 1
    fi
    exit 0
fi

# Pool selection. Default = fast only. --llm and --all are explicit opt-ins;
# a bare glob ("12-*") searches all pools so callers don't need to remember
# which directory a scenario lives in.
#
# --llm additionally fail-fasts if the local LLM backend (Ollama) isn't
# reachable — see e2e::require_ollama in _lib.sh and
# docs/developer/local-ai-ollama.md.
pools=("fast")
glob="*.sh"

case "${1:-}" in
    --llm)
        # shellcheck disable=SC1091
        source "${HERE}/_lib.sh"
        if ! e2e::require_ollama; then
            printf '\n[e2e] --llm requires a reachable Ollama server.\n' >&2
            printf '[e2e] See docs/developer/local-ai-ollama.md to enable it.\n' >&2
            exit 2
        fi
        pools=("llm")
        glob="${2:-*.sh}"
        ;;
    --all)
        pools=("fast" "llm")
        glob="${2:-*.sh}"
        ;;
    "")
        ;;
    -*)
        printf '[e2e] Unknown option: %s\n' "$1" >&2
        printf '[e2e] Usage: ./run.sh [--llm|--all|--sweep] [scenario-glob]\n' >&2
        exit 2
        ;;
    *)
        # Positional glob: search across all pools so a name like "12-*"
        # works regardless of which directory the scenario lives in.
        pools=("fast" "llm")
        glob="$1"
        ;;
esac

total_pass=0
total_fail=0
failures=()
ran=0

e2e_log_prefix="[e2e] run_id=${E2E_RUN_ID} prefix=${E2E_PREFIX} pools=${pools[*]} glob=${glob}"
printf '%s\n' "${e2e_log_prefix}"

shopt -s nullglob
for pool in "${pools[@]}"; do
    pool_dir="${HERE}/scenarios/${pool}"
    [[ -d "${pool_dir}" ]] || continue
    for script in "${pool_dir}/"${glob}; do
        [[ -f "${script}" ]] || continue
        # Skip the llm/README.md placeholder and any other non-.sh files that
        # a loose glob might drag in.
        [[ "${script}" == *.sh ]] || continue
        name="${pool}/$(basename "${script}" .sh)"
        printf '\n===== %s =====\n' "${name}"
        if bash "${script}"; then
            total_pass=$((total_pass+1))
        else
            total_fail=$((total_fail+1))
            failures+=("${name}")
        fi
        ran=$((ran+1))
    done
done

if (( ran == 0 )); then
    printf '\n[e2e] No scenarios matched pools=%s glob=%s\n' "${pools[*]}" "${glob}" >&2
    exit 1
fi

printf '\n===== SUMMARY =====\n'
printf '%d scenarios passed, %d failed\n' "${total_pass}" "${total_fail}"
if (( total_fail > 0 )); then
    printf 'Failed scenarios:\n'
    for f in "${failures[@]}"; do printf '  - %s\n' "${f}"; done
    exit 1
fi
exit 0
