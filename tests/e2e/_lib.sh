# shellcheck shell=bash
# Shared helpers for tests/e2e scenarios. Source me.

set -o pipefail

: "${E2E_BASE_URL:=http://localhost}"
: "${E2E_CURL_OPTS:=--silent --show-error --max-time 30}"

# Repo root (two levels up from tests/e2e). Scenarios may override.
: "${E2E_REPO_ROOT:=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

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

e2e::expect_status() {
    local expected="$1" actual="$2" desc="$3"
    if [[ "${actual}" == "${expected}" ]]; then e2e::ok "${desc} (status ${actual})"; else e2e::fail "${desc} — expected ${expected}, got ${actual}"; fi
}

e2e::expect_contains() {
    local needle="$1" haystack="$2" desc="$3"
    if [[ "${haystack}" == *"${needle}"* ]]; then e2e::ok "${desc}"; else e2e::fail "${desc} — did not find '${needle}' in: ${haystack:0:500}"; fi
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
