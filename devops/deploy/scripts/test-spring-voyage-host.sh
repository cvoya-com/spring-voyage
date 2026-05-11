#!/usr/bin/env bash
# Idempotence + lifecycle tests for deployment/spring-voyage-host.sh.
#
# Drives the host script through every verb that an operator might
# invoke twice in a row, asserting the script never crashes, never
# silently corrupts state, and the persisted dispatcher.env/PID file
# end up in the shape downstream tooling (deploy.sh,
# docker-compose.yml) depends on.
#
# Why a separate script (rather than xUnit) — the host script is the
# one piece of the deployment surface that is intentionally not part
# of the .NET solution. It runs *before* the worker container starts,
# can be invoked without `dotnet test` being available, and needs to
# work on macOS arm64 (where the production target lives) just as
# well as on the CI runner. A bash test driver keeps that constraint
# honest. The .github/workflows/ci.yml job `host-script-idempotence`
# runs this script on Ubuntu; macOS verification is documented in
# deployment/README.md.
#
# Cases covered:
#   1. `build` produces the dll under ${PUBLISH_DIR}.
#   2. `start` launches the dispatcher, writes a 64-hex-char token to
#      dispatcher.env (mode 0600), and /health returns 200.
#   3. Second `start` is a no-op (already running), PID unchanged.
#   4. `status` exits 0 and prints PID + URL + version.
#   5. `restart` produces a new PID, /health still 200, and the
#      persisted token in dispatcher.env is preserved.
#   6. `stop` removes the PID file and the port stops accepting.
#   7. Second `stop` is a no-op (not running), exits 0.
#   8. Stale-PID-file recovery: kill the dispatcher externally,
#      re-run `status` (must report not-running and clean the file)
#      and `start` (must come back up cleanly).
#
# Exit code: 0 on success, non-zero on the first failed assertion.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOYMENT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${DEPLOYMENT_DIR}/.." && pwd)"

HOST_SCRIPT="${DEPLOYMENT_DIR}/spring-voyage-host.sh"
[[ -x "${HOST_SCRIPT}" ]] || { echo "host script not executable: ${HOST_SCRIPT}" >&2; exit 1; }

# Isolated state and publish directories so the test never collides
# with a developer's local ~/.spring-voyage tree. Cleaned up on exit.
STATE_DIR="$(mktemp -d -t spring-voyage-host-state.XXXXXX)"
PUBLISH_DIR="$(mktemp -d -t spring-voyage-host-publish.XXXXXX)"
WORKSPACE_ROOT="$(mktemp -d -t spring-voyage-host-workspaces.XXXXXX)"

# Pin to localhost on a high test port so concurrent test runs on the
# same machine don't fight over 8090.
TEST_PORT="${SPRING_VOYAGE_HOST_TEST_PORT:-18090}"
TEST_HOST="127.0.0.1"

export SPRING_HOST_STATE_DIR="${STATE_DIR}"
export SPRING_DISPATCHER_PUBLISH_DIR="${PUBLISH_DIR}"
export SPRING_DISPATCHER_WORKSPACE_ROOT="${WORKSPACE_ROOT}"
export SPRING_DISPATCHER_HOST="${TEST_HOST}"
export SPRING_DISPATCHER_PORT="${TEST_PORT}"
unset SPRING_DISPATCHER_WORKER_TOKEN
unset SPRING_DISPATCHER_BIN
# Empty SPRING_ENV_FILE so the host script doesn't accidentally pick
# up a developer's local deployment/spring.env.
export SPRING_ENV_FILE="${STATE_DIR}/empty.env"
: >"${SPRING_ENV_FILE}"

# The dispatcher requires a container runtime binary on PATH at boot
# (ContainerRuntimeBinaryConfigurationRequirement, mandatory). Pick
# whatever is installed on this host — Ubuntu CI runners ship docker;
# developer macOS hosts ship podman. Either is fine for the lifecycle
# probe (we never actually dispatch a container in this script).
if command -v docker >/dev/null 2>&1; then
    export ContainerRuntime__RuntimeType=docker
elif command -v podman >/dev/null 2>&1; then
    export ContainerRuntime__RuntimeType=podman
else
    echo "neither docker nor podman is on PATH; cannot run host-script idempotence test" >&2
    exit 2
fi

PID_FILE="${STATE_DIR}/spring-dispatcher.pid"
LOG_FILE="${STATE_DIR}/spring-dispatcher.log"
ENV_FILE="${STATE_DIR}/dispatcher.env"

cleanup() {
    local exit_code=$?
    set +e
    if [[ -f "${PID_FILE}" ]]; then
        "${HOST_SCRIPT}" stop >/dev/null 2>&1 || true
    fi
    # Last-ditch: if anything we spawned is still alive, kill it so
    # the runner doesn't time out on background processes holding the
    # port.
    if [[ -f "${PID_FILE}" ]]; then
        local stale_pid
        stale_pid="$(cat "${PID_FILE}" 2>/dev/null || true)"
        [[ -n "${stale_pid}" ]] && kill -KILL "${stale_pid}" 2>/dev/null || true
    fi
    if [[ ${KEEP_TEST_DIRS:-0} -eq 0 ]]; then
        rm -rf "${STATE_DIR}" "${PUBLISH_DIR}" "${WORKSPACE_ROOT}"
    else
        echo "KEEP_TEST_DIRS set — preserving:"
        echo "  state:     ${STATE_DIR}"
        echo "  publish:   ${PUBLISH_DIR}"
        echo "  workspace: ${WORKSPACE_ROOT}"
    fi
    exit "${exit_code}"
}
trap cleanup EXIT INT TERM

step() { printf '\n=== %s ===\n' "$*"; }
fail() { printf '\n[FAIL] %s\n' "$*" >&2; exit 1; }
ok()   { printf '[ ok ] %s\n' "$*"; }

read_pid() { cat "${PID_FILE}" 2>/dev/null || true; }

# Portable octal-mode lookup. GNU stat (Linux) takes `-c '%a'`; BSD stat
# (macOS) takes `-f '%Lp'`. We can't just chain them with `||` on `-f`
# because GNU stat *also* accepts `-f` — there it means "display
# filesystem status" and exits 0 with unrelated output, masking the BSD
# fallback. Try GNU first (fails fast on macOS where `-c` is unknown),
# then fall back to BSD.
file_mode_octal() {
    local path="$1"
    stat -c '%a' "${path}" 2>/dev/null \
        || stat -f '%Lp' "${path}" 2>/dev/null \
        || echo '???'
}

probe_health_code() {
    # `curl -w '%{http_code}'` always emits a 3-digit code on stdout
    # (000 when the connection itself failed). Drop -f so a non-2xx
    # doesn't make curl exit non-zero — we want to inspect the code,
    # not assume curl already validated it. `|| true` guards `set -e`
    # against curl's exit status on connection failure.
    curl -s -o /dev/null -w '%{http_code}' --max-time "${1:-5}" \
        "http://${TEST_HOST}:${TEST_PORT}/health" 2>/dev/null || true
}

assert_health_200() {
    local probed
    probed="$(probe_health_code 5)"
    [[ "${probed}" == "200" ]] || fail "expected /health 200, got '${probed}' (log tail: $(tail -n 20 "${LOG_FILE}" 2>/dev/null | tr '\n' ' '))"
}

assert_health_unreachable() {
    local probed
    probed="$(probe_health_code 2)"
    [[ "${probed}" == "000" ]] || fail "expected /health unreachable after stop, got '${probed}'"
}

step "case 1 — build emits the dispatcher dll"
"${HOST_SCRIPT}" build >/dev/null
[[ -f "${PUBLISH_DIR}/Cvoya.Spring.Dispatcher.dll" ]] \
    || fail "build did not produce ${PUBLISH_DIR}/Cvoya.Spring.Dispatcher.dll"
ok "build produced dispatcher dll"

step "case 2 — first start writes pid + dispatcher.env (0600, 64 hex chars) and /health is 200"
"${HOST_SCRIPT}" start >/dev/null
[[ -f "${PID_FILE}" ]] || fail "start did not write ${PID_FILE}"
[[ -f "${ENV_FILE}" ]] || fail "start did not write ${ENV_FILE}"
ENV_MODE="$(file_mode_octal "${ENV_FILE}")"
[[ "${ENV_MODE}" == "600" ]] || fail "dispatcher.env mode is '${ENV_MODE}', expected '600'"
TOKEN_LINE="$(grep -E '^SPRING_DISPATCHER_WORKER_TOKEN=' "${ENV_FILE}" | tail -n1)"
TOKEN_VALUE="${TOKEN_LINE#SPRING_DISPATCHER_WORKER_TOKEN=}"
[[ "${TOKEN_VALUE}" =~ ^[0-9a-fA-F]{64}$ ]] \
    || fail "auto-generated token is not 64 hex chars: '${TOKEN_VALUE}'"
PID_AFTER_FIRST_START="$(read_pid)"
assert_health_200
ok "start: pid=${PID_AFTER_FIRST_START}, env mode 600, token is 64 hex chars, /health 200"

step "case 3 — second start is a no-op (PID unchanged)"
"${HOST_SCRIPT}" start >/dev/null
PID_AFTER_SECOND_START="$(read_pid)"
[[ "${PID_AFTER_FIRST_START}" == "${PID_AFTER_SECOND_START}" ]] \
    || fail "second start changed PID (was ${PID_AFTER_FIRST_START}, now ${PID_AFTER_SECOND_START})"
ok "second start no-op (pid unchanged)"

step "case 4 — status reports pid, url, version"
STATUS_OUT="$("${HOST_SCRIPT}" status 2>&1)"
echo "${STATUS_OUT}" | grep -q "pid=${PID_AFTER_FIRST_START}" \
    || fail "status missing 'pid=${PID_AFTER_FIRST_START}': ${STATUS_OUT}"
echo "${STATUS_OUT}" | grep -q "http://${TEST_HOST}:${TEST_PORT}" \
    || fail "status missing url 'http://${TEST_HOST}:${TEST_PORT}': ${STATUS_OUT}"
echo "${STATUS_OUT}" | grep -q "version:" \
    || fail "status missing 'version:' line: ${STATUS_OUT}"
ok "status output includes pid, url, version"

step "case 5 — restart yields a new PID, /health still 200, persisted token preserved"
TOKEN_BEFORE_RESTART="${TOKEN_VALUE}"
"${HOST_SCRIPT}" restart >/dev/null
PID_AFTER_RESTART="$(read_pid)"
[[ -n "${PID_AFTER_RESTART}" ]] || fail "restart left no PID file"
[[ "${PID_AFTER_RESTART}" != "${PID_AFTER_FIRST_START}" ]] \
    || fail "restart did not change PID (still ${PID_AFTER_FIRST_START})"
TOKEN_AFTER_RESTART="$(grep -E '^SPRING_DISPATCHER_WORKER_TOKEN=' "${ENV_FILE}" | tail -n1)"
TOKEN_AFTER_RESTART="${TOKEN_AFTER_RESTART#SPRING_DISPATCHER_WORKER_TOKEN=}"
[[ "${TOKEN_AFTER_RESTART}" == "${TOKEN_BEFORE_RESTART}" ]] \
    || fail "restart rotated the token (was ${TOKEN_BEFORE_RESTART}, now ${TOKEN_AFTER_RESTART})"
assert_health_200
ok "restart: new pid=${PID_AFTER_RESTART}, /health 200, token preserved"

step "case 6 — stop removes the PID file and the port stops accepting"
"${HOST_SCRIPT}" stop >/dev/null
[[ ! -f "${PID_FILE}" ]] || fail "stop did not remove ${PID_FILE}"
sleep 1
assert_health_unreachable
ok "stop removed pid file and port is closed"

step "case 7 — second stop is a no-op (exit 0)"
"${HOST_SCRIPT}" stop >/dev/null
ok "second stop no-op"

step "case 8 — stale PID file recovery"
"${HOST_SCRIPT}" start >/dev/null
STALE_PID="$(read_pid)"
# Kill the dispatcher externally without touching the PID file so the
# script sees a stale pointer on the next call.
kill -KILL "${STALE_PID}" 2>/dev/null || true
sleep 1
# `status` is the cheap probe the script uses to discover the
# orphaned-file case; it must report not-running and clean up.
if "${HOST_SCRIPT}" status >/dev/null 2>&1; then
    fail "status returned 0 for a killed dispatcher (stale-pid recovery did not engage)"
fi
[[ ! -f "${PID_FILE}" ]] || fail "status did not clean up stale PID file"
"${HOST_SCRIPT}" start >/dev/null
NEW_PID="$(read_pid)"
[[ -n "${NEW_PID}" ]] || fail "start after stale-pid did not write a new PID"
[[ "${NEW_PID}" != "${STALE_PID}" ]] || fail "start after stale-pid reused the dead PID"
assert_health_200
ok "stale-pid: status cleaned the file, start came back up (pid=${NEW_PID})"

# Trap-driven cleanup will stop the dispatcher and rm the temp dirs.
echo
echo "All host-script idempotence cases passed."
