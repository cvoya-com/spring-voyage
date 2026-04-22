#!/usr/bin/env bash
# Spring Voyage — host-process services.
#
# spring-dispatcher runs as a long-lived host process (one per dev machine
# or VPS), not inside a container. Reasons (issue #1063):
#
#   - On macOS arm64, Podman runs inside a libkrun VM and the rootless
#     podman socket bind-mounted into a container fails with EACCES even
#     when SELinux relabel options are correct. Running the dispatcher on
#     the host sidesteps the socket-passthrough entirely — podman is a
#     plain local process call.
#   - The same code path then works identically on Linux and Windows.
#     A single topology across Linux/macOS/Windows is the only way the
#     local dev experience stays predictable.
#   - Container images stop carrying the podman CLI, which removes a
#     long-standing rootful/rootless dependency surface from the OSS
#     image build.
#
# This script owns the dispatcher's binary build, bind address, port,
# workspace root, PID file, and log file. `deploy.sh up` calls
# `spring-voyage-host.sh start` after the platform's data services come
# up; `deploy.sh down` calls `spring-voyage-host.sh stop`. Operators can
# invoke the verbs directly when they want to bounce the dispatcher
# without restarting the rest of the stack.
#
# Usage:
#   ./spring-voyage-host.sh start [--rebuild]   # publish if needed, then run
#   ./spring-voyage-host.sh stop                # SIGTERM, then SIGKILL after 10s
#   ./spring-voyage-host.sh restart [--rebuild] # stop + start
#   ./spring-voyage-host.sh status              # PID, port, workspace root
#   ./spring-voyage-host.sh logs [-f]           # cat or follow the log file
#   ./spring-voyage-host.sh build               # publish (build) only
#
# Tunable knobs (env or spring.env):
#   SPRING_DISPATCHER_HOST           (default 0.0.0.0)
#   SPRING_DISPATCHER_PORT           (default 8090)
#   SPRING_DISPATCHER_WORKSPACE_ROOT (default ~/.spring-voyage/workspaces)
#   SPRING_DISPATCHER_WORKER_TOKEN   (default worker-token; CHANGE FOR SHARED HOSTS)
#   SPRING_DEFAULT_TENANT_ID         (default default)
#   SPRING_HOST_STATE_DIR            (default ~/.spring-voyage/host)
#   SPRING_DISPATCHER_BIN            (override path to dotnet dll)
#   SPRING_DISPATCHER_RUNTIME        (override RuntimeIdentifier for self-contained publish)
#   SPRING_DISPATCHER_PUBLISH_DIR    (override publish output directory)
#   SPRING_ENV_FILE                  (default ./spring.env, optional)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

ENV_FILE="${SPRING_ENV_FILE:-${SCRIPT_DIR}/spring.env}"

STATE_DIR="${SPRING_HOST_STATE_DIR:-${HOME}/.spring-voyage/host}"
PID_FILE="${STATE_DIR}/spring-dispatcher.pid"
LOG_FILE="${STATE_DIR}/spring-dispatcher.log"

DISPATCHER_HOST_DEFAULT="0.0.0.0"
DISPATCHER_PORT_DEFAULT="8090"
WORKSPACE_ROOT_DEFAULT="${HOME}/.spring-voyage/workspaces"
TOKEN_DEFAULT="worker-token"
TENANT_DEFAULT="default"

DISPATCHER_PROJECT="${REPO_ROOT}/src/Cvoya.Spring.Dispatcher/Cvoya.Spring.Dispatcher.csproj"
PUBLISH_DIR_DEFAULT="${REPO_ROOT}/.spring-voyage/dispatcher/publish"

log()  { printf '[host] %s\n' "$*" >&2; }
die()  { printf '[host][error] %s\n' "$*" >&2; exit 1; }

require() {
    command -v "$1" >/dev/null 2>&1 || die "required command '$1' not found on PATH"
}

# Source spring.env when present so SPRING_DISPATCHER_* defaults set in
# the shared env file flow through. Failure to find the file is a no-op
# — the dispatcher does not depend on spring.env to come up.
load_env() {
    if [[ -f "${ENV_FILE}" ]]; then
        set -a
        # shellcheck disable=SC1090
        source "${ENV_FILE}"
        set +a
    fi
}

resolve_settings() {
    DISPATCHER_HOST="${SPRING_DISPATCHER_HOST:-${DISPATCHER_HOST_DEFAULT}}"
    DISPATCHER_PORT="${SPRING_DISPATCHER_PORT:-${DISPATCHER_PORT_DEFAULT}}"
    WORKSPACE_ROOT="${SPRING_DISPATCHER_WORKSPACE_ROOT:-${WORKSPACE_ROOT_DEFAULT}}"
    TOKEN="${SPRING_DISPATCHER_WORKER_TOKEN:-${TOKEN_DEFAULT}}"
    TENANT="${SPRING_DEFAULT_TENANT_ID:-${TENANT_DEFAULT}}"
    PUBLISH_DIR="${SPRING_DISPATCHER_PUBLISH_DIR:-${PUBLISH_DIR_DEFAULT}}"
}

ensure_state_dir() {
    mkdir -p "${STATE_DIR}"
    mkdir -p "${WORKSPACE_ROOT}"
}

# Discover the dispatcher binary. Order:
#   1. SPRING_DISPATCHER_BIN (must be a readable file)
#   2. ${PUBLISH_DIR}/Cvoya.Spring.Dispatcher.dll (produced by `build`)
# Anything else returns 1 so callers can fall through to publish.
discover_binary() {
    if [[ -n "${SPRING_DISPATCHER_BIN:-}" ]]; then
        if [[ -f "${SPRING_DISPATCHER_BIN}" ]]; then
            BIN="${SPRING_DISPATCHER_BIN}"
            return 0
        fi
        die "SPRING_DISPATCHER_BIN='${SPRING_DISPATCHER_BIN}' does not exist"
    fi
    local candidate="${PUBLISH_DIR}/Cvoya.Spring.Dispatcher.dll"
    if [[ -f "${candidate}" ]]; then
        BIN="${candidate}"
        return 0
    fi
    return 1
}

publish_dispatcher() {
    require dotnet
    [[ -f "${DISPATCHER_PROJECT}" ]] || die "dispatcher project not found at ${DISPATCHER_PROJECT}"

    log "publishing dispatcher to ${PUBLISH_DIR}"
    mkdir -p "${PUBLISH_DIR}"

    # Framework-dependent publish — keeps the output small (~1MB) and
    # relies on the host's `dotnet` runtime, which the script also
    # requires at start time. Operators who want a self-contained binary
    # can publish manually with -r <rid> --self-contained and point
    # SPRING_DISPATCHER_BIN at the resulting executable.
    dotnet publish "${DISPATCHER_PROJECT}" \
        --configuration Release \
        --output "${PUBLISH_DIR}" \
        --nologo \
        --verbosity quiet
}

# Returns 0 when a process matching the PID file is running, 1 otherwise.
# Cleans up stale PID files (process gone, file kept around from a prior
# crash) so callers can rely on the post-condition.
is_running() {
    [[ -f "${PID_FILE}" ]] || return 1
    local pid
    pid="$(cat "${PID_FILE}" 2>/dev/null || true)"
    if [[ -z "${pid}" ]] || ! kill -0 "${pid}" 2>/dev/null; then
        rm -f "${PID_FILE}"
        return 1
    fi
    PID="${pid}"
    return 0
}

# Block until http://${DISPATCHER_HOST}:${DISPATCHER_PORT}/health responds
# 200, or fail after timeout. host=0.0.0.0 needs to be probed via 127.0.0.1.
wait_health() {
    local probe_host="${DISPATCHER_HOST}"
    if [[ "${probe_host}" == "0.0.0.0" || "${probe_host}" == "::" ]]; then
        probe_host="127.0.0.1"
    fi
    local url="http://${probe_host}:${DISPATCHER_PORT}/health"
    local timeout="${1:-30}"
    local waited=0
    if ! command -v curl >/dev/null 2>&1; then
        log "curl not found on PATH; skipping health probe (assuming ready after sleep)"
        sleep 2
        return 0
    fi
    while (( waited < timeout )); do
        if curl -sf -o /dev/null --max-time 2 "${url}" >/dev/null 2>&1; then
            log "dispatcher is healthy on ${url}"
            return 0
        fi
        sleep 1
        waited=$(( waited + 1 ))
    done
    die "dispatcher did not report healthy on ${url} within ${timeout}s — see ${LOG_FILE}"
}

cmd_build() {
    load_env
    resolve_settings
    ensure_state_dir
    publish_dispatcher
    log "build complete: ${PUBLISH_DIR}/Cvoya.Spring.Dispatcher.dll"
}

cmd_start() {
    local rebuild=0
    if [[ "${1:-}" == "--rebuild" ]]; then
        rebuild=1
        shift
    fi

    load_env
    resolve_settings
    ensure_state_dir

    if is_running; then
        log "dispatcher already running (pid=${PID}) on ${DISPATCHER_HOST}:${DISPATCHER_PORT}"
        return 0
    fi

    if (( rebuild == 1 )) || ! discover_binary; then
        publish_dispatcher
        discover_binary || die "publish succeeded but dispatcher dll not found at ${PUBLISH_DIR}"
    fi

    require dotnet

    local urls="http://${DISPATCHER_HOST}:${DISPATCHER_PORT}"
    log "starting dispatcher on ${urls}"
    log "  workspace root: ${WORKSPACE_ROOT}"
    log "  log file:       ${LOG_FILE}"

    # Spawn the dispatcher detached, capturing stdout/stderr to LOG_FILE
    # and writing the PID immediately. setsid puts the child in its own
    # session so closing the script's terminal does not deliver SIGHUP.
    (
        export ASPNETCORE_URLS="${urls}"
        export DOTNET_NOLOGO=true
        export DOTNET_CLI_TELEMETRY_OPTOUT=true
        export Dispatcher__WorkspaceRoot="${WORKSPACE_ROOT}"
        export "Dispatcher__Tokens__${TOKEN}__TenantId=${TENANT}"
        # Pass through any caller-set ContainerRuntime__* / Logging__*
        # values so operators can tune the dispatcher without re-editing
        # this script.
        if command -v setsid >/dev/null 2>&1; then
            setsid dotnet "${BIN}" >"${LOG_FILE}" 2>&1 &
        else
            # macOS does not ship setsid; nohup is the portable fallback.
            nohup dotnet "${BIN}" >"${LOG_FILE}" 2>&1 &
        fi
        echo $! >"${PID_FILE}"
    )

    # Poll briefly for the PID file (the subshell writes it asynchronously).
    local waited=0
    while (( waited < 5 )) && [[ ! -f "${PID_FILE}" ]]; do
        sleep 1
        waited=$(( waited + 1 ))
    done
    [[ -f "${PID_FILE}" ]] || die "dispatcher pid file was not written — see ${LOG_FILE}"

    wait_health 30
    log "dispatcher started (pid=$(cat "${PID_FILE}"))"
}

cmd_stop() {
    load_env
    resolve_settings

    if ! is_running; then
        log "dispatcher is not running"
        return 0
    fi

    log "stopping dispatcher (pid=${PID})"
    kill -TERM "${PID}" 2>/dev/null || true

    local waited=0
    while (( waited < 10 )) && kill -0 "${PID}" 2>/dev/null; do
        sleep 1
        waited=$(( waited + 1 ))
    done

    if kill -0 "${PID}" 2>/dev/null; then
        log "dispatcher did not exit within 10s; sending SIGKILL"
        kill -KILL "${PID}" 2>/dev/null || true
    fi

    rm -f "${PID_FILE}"
    log "dispatcher stopped"
}

cmd_restart() {
    cmd_stop
    cmd_start "$@"
}

cmd_status() {
    load_env
    resolve_settings
    if is_running; then
        log "dispatcher running (pid=${PID})"
        log "  url:            http://${DISPATCHER_HOST}:${DISPATCHER_PORT}"
        log "  workspace root: ${WORKSPACE_ROOT}"
        log "  log file:       ${LOG_FILE}"
        return 0
    fi
    log "dispatcher not running"
    return 1
}

cmd_logs() {
    if [[ ! -f "${LOG_FILE}" ]]; then
        die "no log file at ${LOG_FILE} — start the dispatcher first"
    fi
    if [[ "${1:-}" == "-f" || "${1:-}" == "--follow" ]]; then
        tail -F "${LOG_FILE}"
    else
        cat "${LOG_FILE}"
    fi
}

usage() {
    cat <<EOF
Spring Voyage — host-process services

Commands:
  start [--rebuild]    Publish (if needed) and start spring-dispatcher.
  stop                 Stop spring-dispatcher.
  restart [--rebuild]  Stop, then start.
  status               Show pid, url, workspace root.
  logs [-f]            Print or follow the dispatcher log.
  build                Publish dispatcher only (no run).

Environment overrides:
  SPRING_DISPATCHER_HOST            (default ${DISPATCHER_HOST_DEFAULT})
  SPRING_DISPATCHER_PORT            (default ${DISPATCHER_PORT_DEFAULT})
  SPRING_DISPATCHER_WORKSPACE_ROOT  (default ${WORKSPACE_ROOT_DEFAULT})
  SPRING_DISPATCHER_WORKER_TOKEN    (default ${TOKEN_DEFAULT})
  SPRING_DEFAULT_TENANT_ID          (default ${TENANT_DEFAULT})
  SPRING_HOST_STATE_DIR             (default ${STATE_DIR})
  SPRING_DISPATCHER_BIN             override published dll path
  SPRING_DISPATCHER_PUBLISH_DIR     override publish output dir
  SPRING_ENV_FILE                   default ${ENV_FILE}
EOF
}

main() {
    local cmd="${1:-}"
    shift || true
    case "${cmd}" in
        start)             cmd_start "$@" ;;
        stop)              cmd_stop "$@" ;;
        restart)           cmd_restart "$@" ;;
        status)            cmd_status "$@" ;;
        logs)              cmd_logs "$@" ;;
        build)             cmd_build "$@" ;;
        ""|-h|--help|help) usage ;;
        *)                 usage; exit 2 ;;
    esac
}

main "$@"
