# shellcheck shell=bash
# Shared helpers for tests/e2e scenarios. Source me.

set -o pipefail

: "${E2E_BASE_URL:=http://localhost}"
: "${E2E_CURL_OPTS:=--silent --show-error --max-time 30}"

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
