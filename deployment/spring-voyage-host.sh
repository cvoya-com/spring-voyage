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
#   SPRING_DISPATCHER_WORKER_TOKEN   (auto-generated on first start, persisted
#                                     to ${STATE_DIR}/dispatcher.env mode 0600)
#   SPRING_DEFAULT_TENANT_ID         (default default)
#   SPRING_HOST_STATE_DIR            (default ~/.spring-voyage/host)
#   SPRING_DISPATCHER_BIN            (override path to dispatcher binary; .dll
#                                     runs under `dotnet`, anything else runs
#                                     directly — supports self-contained
#                                     single-file publishes)
#   SPRING_DISPATCHER_PUBLISH_DIR    (override publish output directory)
#   SPRING_ENV_FILE                  (default ./spring.env, optional)
#
# State files written under ${SPRING_HOST_STATE_DIR}:
#   spring-dispatcher.pid   — PID of the running dispatcher
#   spring-dispatcher.log   — stdout+stderr of the dispatcher
#   dispatcher.env          — resolved env (host, port, token, tenant,
#                             workspace-root). Mode 0600. Sourced by
#                             deploy.sh so the worker container picks up
#                             the same bearer token without it being
#                             checked in or hardcoded.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

ENV_FILE="${SPRING_ENV_FILE:-${SCRIPT_DIR}/spring.env}"

STATE_DIR="${SPRING_HOST_STATE_DIR:-${HOME}/.spring-voyage/host}"
PID_FILE="${STATE_DIR}/spring-dispatcher.pid"
LOG_FILE="${STATE_DIR}/spring-dispatcher.log"
DISPATCHER_ENV_FILE="${STATE_DIR}/dispatcher.env"

DISPATCHER_HOST_DEFAULT="0.0.0.0"
DISPATCHER_PORT_DEFAULT="8090"
WORKSPACE_ROOT_DEFAULT="${HOME}/.spring-voyage/workspaces"
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

# Read the previously persisted token from ${DISPATCHER_ENV_FILE} so a
# `restart` keeps the same bearer secret across the stop/start. Falls
# through to "" if the file is missing or malformed; callers handle the
# generation path. Only reads the token line so a hand-edited env file
# can't smuggle other variables into the script's scope.
read_persisted_token() {
    [[ -f "${DISPATCHER_ENV_FILE}" ]] || return 0
    local line
    line="$(grep -E '^SPRING_DISPATCHER_WORKER_TOKEN=' "${DISPATCHER_ENV_FILE}" 2>/dev/null | tail -n1 || true)"
    [[ -n "${line}" ]] || return 0
    printf '%s' "${line#SPRING_DISPATCHER_WORKER_TOKEN=}"
}

# Generate a 256-bit hex token. Prefers openssl (POSIX, present on every
# supported host); falls back to xxd over /dev/urandom (BSD + GNU). Dies
# if neither is available — operators on stripped containers can supply
# SPRING_DISPATCHER_WORKER_TOKEN explicitly.
generate_token() {
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -hex 32
    elif command -v xxd >/dev/null 2>&1; then
        xxd -l 32 -p /dev/urandom | tr -d '\n'
    else
        die "cannot generate bearer token: neither 'openssl' nor 'xxd' is on PATH. Set SPRING_DISPATCHER_WORKER_TOKEN explicitly."
    fi
}

resolve_settings() {
    DISPATCHER_HOST="${SPRING_DISPATCHER_HOST:-${DISPATCHER_HOST_DEFAULT}}"
    DISPATCHER_PORT="${SPRING_DISPATCHER_PORT:-${DISPATCHER_PORT_DEFAULT}}"
    WORKSPACE_ROOT="${SPRING_DISPATCHER_WORKSPACE_ROOT:-${WORKSPACE_ROOT_DEFAULT}}"
    TENANT="${SPRING_DEFAULT_TENANT_ID:-${TENANT_DEFAULT}}"
    PUBLISH_DIR="${SPRING_DISPATCHER_PUBLISH_DIR:-${PUBLISH_DIR_DEFAULT}}"

    # Token resolution order:
    #   1. SPRING_DISPATCHER_WORKER_TOKEN from env / spring.env (operator
    #      override; takes precedence over any persisted value).
    #   2. The token previously written to ${DISPATCHER_ENV_FILE} on a
    #      prior `start` (so `restart` keeps the same secret).
    #   3. Freshly generated 256-bit hex string, persisted on `start`.
    if [[ -n "${SPRING_DISPATCHER_WORKER_TOKEN:-}" ]]; then
        TOKEN="${SPRING_DISPATCHER_WORKER_TOKEN}"
        TOKEN_SOURCE="env"
        return
    fi
    local persisted
    persisted="$(read_persisted_token)"
    if [[ -n "${persisted}" ]]; then
        TOKEN="${persisted}"
        TOKEN_SOURCE="persisted"
        return
    fi
    TOKEN="$(generate_token)"
    TOKEN_SOURCE="generated"
}

# Write the resolved env to ${DISPATCHER_ENV_FILE} so deploy.sh and other
# tooling can source the same token + port without hardcoding either.
# Mode 0600 because the token grants tenant-scoped dispatch authority.
#
# The `umask 077` is confined to a subshell on purpose: prior to this fix
# the function ran `umask 077` in the script's own shell, the change leaked
# to every later command, and the dispatcher process spawned downstream
# inherited that umask. The dispatcher then created per-invocation agent
# workspace directories with mode 0700, and the agent user inside the
# launched container (UID 1000, not the dispatcher's host UID) could not
# read `CLAUDE.md` / `.mcp.json`. WorkspaceMaterializer also chmods the
# workspace explicitly now (belt-and-suspenders), but the umask still has
# to be scoped here so other dispatcher-side file creation that hasn't
# been audited isn't silently rendered private to the host user.
write_dispatcher_env() {
    (
        umask 077
        {
            printf '# Generated by spring-voyage-host.sh — do not edit by hand.\n'
            printf '# Sourced by deploy.sh and friends to discover the running\n'
            printf '# dispatcher. Regenerated on every `start`. Delete this file\n'
            printf '# to rotate the bearer token on the next start.\n'
            printf 'SPRING_DISPATCHER_HOST=%s\n' "${DISPATCHER_HOST}"
            printf 'SPRING_DISPATCHER_PORT=%s\n' "${DISPATCHER_PORT}"
            printf 'SPRING_DISPATCHER_WORKER_TOKEN=%s\n' "${TOKEN}"
            printf 'SPRING_DEFAULT_TENANT_ID=%s\n' "${TENANT}"
            printf 'SPRING_DISPATCHER_WORKSPACE_ROOT=%s\n' "${WORKSPACE_ROOT}"
        } >"${DISPATCHER_ENV_FILE}"
        chmod 0600 "${DISPATCHER_ENV_FILE}"
    )
}

ensure_state_dir() {
    mkdir -p "${STATE_DIR}"
    mkdir -p "${WORKSPACE_ROOT}"
}

# Discover the dispatcher binary. Order:
#   1. SPRING_DISPATCHER_BIN (must be a readable file). Either a managed
#      .dll (run under `dotnet`) or a native self-contained executable
#      (run directly). Useful for operators who download a self-contained
#      release artifact and want zero `dotnet` runtime dependency.
#   2. ${PUBLISH_DIR}/Cvoya.Spring.Dispatcher       (native, self-contained)
#   3. ${PUBLISH_DIR}/Cvoya.Spring.Dispatcher.exe   (native, self-contained, Windows)
#   4. ${PUBLISH_DIR}/Cvoya.Spring.Dispatcher.dll   (framework-dependent, `build` default)
# Anything else returns 1 so callers can fall through to publish. Sets BIN
# and BIN_KIND ("native" | "dll") so cmd_start knows whether to prepend
# `dotnet`.
discover_binary() {
    BIN=""
    BIN_KIND=""
    if [[ -n "${SPRING_DISPATCHER_BIN:-}" ]]; then
        if [[ -f "${SPRING_DISPATCHER_BIN}" ]]; then
            BIN="${SPRING_DISPATCHER_BIN}"
            classify_binary
            return 0
        fi
        die "SPRING_DISPATCHER_BIN='${SPRING_DISPATCHER_BIN}' does not exist"
    fi
    local candidate
    for candidate in \
        "${PUBLISH_DIR}/Cvoya.Spring.Dispatcher" \
        "${PUBLISH_DIR}/Cvoya.Spring.Dispatcher.exe" \
        "${PUBLISH_DIR}/Cvoya.Spring.Dispatcher.dll"; do
        if [[ -f "${candidate}" ]]; then
            BIN="${candidate}"
            classify_binary
            return 0
        fi
    done
    return 1
}

# Classify BIN as a managed dll (run under dotnet) or a native binary
# (run directly). The .dll suffix is the only reliable signal — a
# self-contained native binary may or may not carry the +x bit on
# Windows, and a framework-dependent .dll is never executable on its own.
classify_binary() {
    case "${BIN}" in
        *.dll) BIN_KIND="dll" ;;
        *)     BIN_KIND="native" ;;
    esac
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
        discover_binary || die "publish succeeded but dispatcher binary not found at ${PUBLISH_DIR}"
    fi

    if [[ "${BIN_KIND}" == "dll" ]]; then
        require dotnet
    fi

    # Persist the resolved env (host/port/token/tenant/workspace) so
    # deploy.sh and other tooling can source the same values without
    # hardcoding the bearer token. Always rewrites — fresh values on
    # every start.
    write_dispatcher_env
    case "${TOKEN_SOURCE}" in
        env)       log "bearer token sourced from environment" ;;
        persisted) log "bearer token reused from ${DISPATCHER_ENV_FILE}" ;;
        generated) log "bearer token freshly generated and persisted to ${DISPATCHER_ENV_FILE} (mode 0600)" ;;
    esac

    local urls="http://${DISPATCHER_HOST}:${DISPATCHER_PORT}"
    log "starting dispatcher on ${urls}"
    log "  workspace root: ${WORKSPACE_ROOT}"
    log "  log file:       ${LOG_FILE}"
    log "  env file:       ${DISPATCHER_ENV_FILE}"
    log "  binary:         ${BIN} (${BIN_KIND})"

    # Spawn the dispatcher detached, capturing stdout/stderr to LOG_FILE
    # and writing the PID immediately. setsid puts the child in its own
    # session so closing the script's terminal does not deliver SIGHUP.
    # Native self-contained binaries run directly (no `dotnet` prefix);
    # framework-dependent .dlls run under `dotnet`.
    (
        export ASPNETCORE_URLS="${urls}"
        export DOTNET_NOLOGO=true
        export DOTNET_CLI_TELEMETRY_OPTOUT=true
        export Dispatcher__WorkspaceRoot="${WORKSPACE_ROOT}"
        export "Dispatcher__Tokens__${TOKEN}__TenantId=${TENANT}"
        # Pass through any caller-set ContainerRuntime__* / Logging__*
        # values so operators can tune the dispatcher without re-editing
        # this script.
        launcher=nohup
        # macOS does not ship setsid; nohup is the portable fallback.
        if command -v setsid >/dev/null 2>&1; then
            launcher=setsid
        fi
        if [[ "${BIN_KIND}" == "dll" ]]; then
            ${launcher} dotnet "${BIN}" >"${LOG_FILE}" 2>&1 &
        else
            ${launcher} "${BIN}" >"${LOG_FILE}" 2>&1 &
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
        log "  env file:       ${DISPATCHER_ENV_FILE}"
        log "  version:        $(dispatcher_version)"
        return 0
    fi
    log "dispatcher not running"
    return 1
}

# Print the dispatcher's InformationalVersion (or "unknown" if the
# binary cannot be discovered or `--version` fails). Captures stderr to
# /dev/null so the status report stays single-line even when the
# command logs warnings.
dispatcher_version() {
    if ! discover_binary >/dev/null 2>&1; then
        printf 'unknown (binary not built — run %s build)' "$(basename "$0")"
        return 0
    fi
    local out
    if [[ "${BIN_KIND}" == "dll" ]]; then
        out="$(dotnet "${BIN}" --version 2>/dev/null || true)"
    else
        out="$("${BIN}" --version 2>/dev/null || true)"
    fi
    if [[ -z "${out}" ]]; then
        printf 'unknown'
    else
        printf '%s' "${out}"
    fi
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
  SPRING_DISPATCHER_WORKER_TOKEN    auto-generated on first start; persisted
                                    to ${STATE_DIR}/dispatcher.env (mode 0600)
                                    and reused by subsequent starts. Delete
                                    the file or set this var to rotate.
  SPRING_DEFAULT_TENANT_ID          (default ${TENANT_DEFAULT})
  SPRING_HOST_STATE_DIR             (default ${STATE_DIR})
  SPRING_DISPATCHER_BIN             override dispatcher binary (.dll runs
                                    under dotnet, anything else runs directly)
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
