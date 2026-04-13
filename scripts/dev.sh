#!/usr/bin/env bash
# Spring Voyage — local development orchestrator.
#
# Starts the full platform locally for development and testing.
# Default mode uses podman containers; --process mode uses dapr run + dotnet run.
#
# Usage:
#   ./scripts/dev.sh up              # start all services (container mode)
#   ./scripts/dev.sh up --process    # start all services (process mode)
#   ./scripts/dev.sh down            # stop all services
#   ./scripts/dev.sh status          # show what is running
#   ./scripts/dev.sh logs [service]  # follow logs for a service
#   ./scripts/dev.sh build           # build container images (container mode)
#
# Container mode runs everything in podman containers on a spring-dev network.
# Process mode runs Redis in podman and everything else as local processes
# via `dapr run` — useful for hot-reload and debugger attach.
#
# Prerequisites:
#   - podman (or docker with CONTAINER_CMD=docker)
#   - dapr CLI (process mode: hosts; container mode: sidecar management)
#   - dotnet SDK (process mode)
#   - node/npm (process mode, for the web dashboard)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
DEV_DIR="${REPO_ROOT}/.dev"
PID_DIR="${DEV_DIR}/pids"
LOG_DIR="${DEV_DIR}/logs"

CONTAINER_CMD="${CONTAINER_CMD:-podman}"
NETWORK_NAME="spring-dev"

# Service names used in container mode
CONTAINER_SERVICES=(spring-dev-redis spring-dev-worker spring-dev-api spring-dev-web)

# Ports
REDIS_PORT="${REDIS_PORT:-6379}"
WORKER_APP_PORT="${WORKER_APP_PORT:-5001}"
WORKER_DAPR_HTTP_PORT="${WORKER_DAPR_HTTP_PORT:-3500}"
API_APP_PORT="${API_APP_PORT:-5000}"
API_DAPR_HTTP_PORT="${API_DAPR_HTTP_PORT:-3501}"
WEB_PORT="${WEB_PORT:-3000}"

DAPR_RESOURCES_PATH="${REPO_ROOT}/dapr/components/local"
DAPR_CONFIG_PATH="${REPO_ROOT}/dapr/config/local.yaml"

log()  { printf '[dev] %s\n' "$*" >&2; }
die()  { printf '[dev][error] %s\n' "$*" >&2; exit 1; }

require() {
    command -v "$1" >/dev/null 2>&1 || die "required command '$1' not found — install it or check your PATH"
}

ensure_dirs() {
    mkdir -p "${PID_DIR}" "${LOG_DIR}"
}

# ---------- container helpers ----------

container_exists() {
    ${CONTAINER_CMD} container exists "$1" 2>/dev/null
}

remove_container() {
    if container_exists "$1"; then
        log "removing container '$1'"
        ${CONTAINER_CMD} rm -f "$1" >/dev/null
    fi
}

ensure_network() {
    if ${CONTAINER_CMD} network exists "${NETWORK_NAME}" 2>/dev/null; then
        return
    fi
    log "creating network '${NETWORK_NAME}'"
    ${CONTAINER_CMD} network create "${NETWORK_NAME}" >/dev/null
}

run_container() {
    local name="$1"; shift
    remove_container "${name}"
    log "starting '${name}'"
    ${CONTAINER_CMD} run -d --name "${name}" --network "${NETWORK_NAME}" --restart=unless-stopped "$@" >/dev/null
}

wait_healthy() {
    local name="$1" timeout="${2:-60}" waited=0
    while (( waited < timeout )); do
        local status
        status="$(${CONTAINER_CMD} inspect -f '{{.State.Health.Status}}' "${name}" 2>/dev/null || echo "")"
        case "${status}" in
            healthy) return 0 ;;
            unhealthy) die "${name} reported unhealthy" ;;
            "") return 0 ;;
        esac
        sleep 2
        waited=$(( waited + 2 ))
    done
    die "${name} did not become healthy within ${timeout}s"
}

# ---------- process mode helpers ----------

save_pid() {
    local name="$1" pid="$2"
    echo "${pid}" > "${PID_DIR}/${name}.pid"
}

read_pid() {
    local name="$1"
    local pidfile="${PID_DIR}/${name}.pid"
    if [[ -f "${pidfile}" ]]; then
        cat "${pidfile}"
    fi
}

is_running() {
    local pid
    pid="$(read_pid "$1")"
    [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null
}

stop_process() {
    local name="$1"
    local pid
    pid="$(read_pid "${name}")"
    if [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null; then
        log "stopping ${name} (pid ${pid})"
        kill "${pid}" 2>/dev/null || true
        # Wait briefly for clean shutdown
        local waited=0
        while (( waited < 10 )) && kill -0 "${pid}" 2>/dev/null; do
            sleep 1
            waited=$(( waited + 1 ))
        done
        # Force kill if still running
        if kill -0 "${pid}" 2>/dev/null; then
            kill -9 "${pid}" 2>/dev/null || true
        fi
    fi
    rm -f "${PID_DIR}/${name}.pid"
}

# ---------- start services: container mode ----------

start_redis_container() {
    run_container spring-dev-redis \
        -p "${REDIS_PORT}:6379" \
        --health-cmd 'redis-cli ping | grep -q PONG' \
        --health-interval 10s \
        --health-timeout 5s \
        --health-retries 5 \
        docker.io/library/redis:7
    wait_healthy spring-dev-redis 30
}

start_worker_container() {
    # Worker runs via dapr sidecar for actor registration.
    # Build from source first (use `dev.sh build`).
    run_container spring-dev-worker \
        -e "ASPNETCORE_URLS=http://+:${WORKER_APP_PORT}" \
        -e "DAPR_HTTP_PORT=${WORKER_DAPR_HTTP_PORT}" \
        -v "${DAPR_RESOURCES_PATH}:/dapr/components:ro,Z" \
        -v "${DAPR_CONFIG_PATH}:/dapr/config.yaml:ro,Z" \
        -p "${WORKER_APP_PORT}:${WORKER_APP_PORT}" \
        -p "${WORKER_DAPR_HTTP_PORT}:${WORKER_DAPR_HTTP_PORT}" \
        "${SPRING_DEV_IMAGE:-localhost/spring-voyage-dev:latest}" \
        dapr run --app-id spring-worker --app-port "${WORKER_APP_PORT}" \
            --dapr-http-port "${WORKER_DAPR_HTTP_PORT}" \
            --resources-path /dapr/components \
            --config /dapr/config.yaml \
            -- dotnet /app/Cvoya.Spring.Host.Worker.dll --urls "http://+:${WORKER_APP_PORT}"
}

start_api_container() {
    run_container spring-dev-api \
        -e "ASPNETCORE_URLS=http://+:${API_APP_PORT}" \
        -e "DAPR_HTTP_PORT=${API_DAPR_HTTP_PORT}" \
        -v "${DAPR_RESOURCES_PATH}:/dapr/components:ro,Z" \
        -v "${DAPR_CONFIG_PATH}:/dapr/config.yaml:ro,Z" \
        -p "${API_APP_PORT}:${API_APP_PORT}" \
        -p "${API_DAPR_HTTP_PORT}:${API_DAPR_HTTP_PORT}" \
        "${SPRING_DEV_IMAGE:-localhost/spring-voyage-dev:latest}" \
        dapr run --app-id spring-api --app-port "${API_APP_PORT}" \
            --dapr-http-port "${API_DAPR_HTTP_PORT}" \
            --resources-path /dapr/components \
            --config /dapr/config.yaml \
            -- dotnet /app/Cvoya.Spring.Host.Api.dll --local
}

start_web_container() {
    run_container spring-dev-web \
        -e "NEXT_PUBLIC_API_URL=http://spring-dev-api:${API_APP_PORT}" \
        -p "${WEB_PORT}:3000" \
        "${SPRING_DEV_IMAGE:-localhost/spring-voyage-dev:latest}" \
        node /app/web/server.js
}

# ---------- start services: process mode ----------

start_redis_process() {
    require "${CONTAINER_CMD}"
    # Redis always runs as a container even in process mode
    if container_exists spring-dev-redis; then
        local running
        running="$(${CONTAINER_CMD} inspect -f '{{.State.Running}}' spring-dev-redis 2>/dev/null || echo "false")"
        if [[ "${running}" == "true" ]]; then
            log "redis already running"
            return
        fi
    fi
    run_container spring-dev-redis \
        -p "${REDIS_PORT}:6379" \
        --health-cmd 'redis-cli ping | grep -q PONG' \
        --health-interval 10s \
        --health-timeout 5s \
        --health-retries 5 \
        docker.io/library/redis:7
    wait_healthy spring-dev-redis 30
}

start_worker_process() {
    require dapr
    require dotnet
    if is_running worker; then
        log "worker already running"
        return
    fi
    log "starting worker (process mode)"
    dapr run --app-id spring-worker --app-port "${WORKER_APP_PORT}" \
        --dapr-http-port "${WORKER_DAPR_HTTP_PORT}" \
        --resources-path "${DAPR_RESOURCES_PATH}" \
        --config "${DAPR_CONFIG_PATH}" \
        -- dotnet run --project "${REPO_ROOT}/src/Cvoya.Spring.Host.Worker" \
            -- --urls "http://localhost:${WORKER_APP_PORT}" \
        > "${LOG_DIR}/worker.log" 2>&1 &
    save_pid worker $!
    log "worker started (pid $!, log: ${LOG_DIR}/worker.log)"
}

start_api_process() {
    require dapr
    require dotnet
    if is_running api; then
        log "api already running"
        return
    fi
    log "starting api (process mode)"
    dapr run --app-id spring-api --app-port "${API_APP_PORT}" \
        --dapr-http-port "${API_DAPR_HTTP_PORT}" \
        --resources-path "${DAPR_RESOURCES_PATH}" \
        --config "${DAPR_CONFIG_PATH}" \
        -- dotnet run --project "${REPO_ROOT}/src/Cvoya.Spring.Host.Api" \
            -- --local \
        > "${LOG_DIR}/api.log" 2>&1 &
    save_pid api $!
    log "api started (pid $!, log: ${LOG_DIR}/api.log)"
}

start_web_process() {
    require node
    require npm
    if is_running web; then
        log "web already running"
        return
    fi
    log "starting web dashboard (process mode)"
    cd "${REPO_ROOT}/src/Cvoya.Spring.Web"
    npm run dev > "${LOG_DIR}/web.log" 2>&1 &
    save_pid web $!
    cd "${REPO_ROOT}"
    log "web started (pid $!, log: ${LOG_DIR}/web.log)"
}

# ---------- commands ----------

cmd_up() {
    local process_mode=false
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --process) process_mode=true; shift ;;
            *) die "unknown option: $1" ;;
        esac
    done

    ensure_dirs

    if [[ "${process_mode}" == true ]]; then
        log "starting in process mode (hot-reload, debugger-friendly)"
        start_redis_process
        start_worker_process
        sleep 3  # brief pause for worker actor registration
        start_api_process
        start_web_process
        log ""
        log "services:"
        log "  worker   http://localhost:${WORKER_APP_PORT}  (dapr: ${WORKER_DAPR_HTTP_PORT})"
        log "  api      http://localhost:${API_APP_PORT}  (dapr: ${API_DAPR_HTTP_PORT})"
        log "  web      http://localhost:${WEB_PORT}"
        log ""
        log "logs in ${LOG_DIR}/  —  stop with: ./scripts/dev.sh down"
    else
        require "${CONTAINER_CMD}"
        log "starting in container mode"
        ensure_network
        start_redis_container
        start_worker_container
        start_api_container
        start_web_container
        log ""
        log "services:"
        log "  worker   http://localhost:${WORKER_APP_PORT}  (dapr: ${WORKER_DAPR_HTTP_PORT})"
        log "  api      http://localhost:${API_APP_PORT}  (dapr: ${API_DAPR_HTTP_PORT})"
        log "  web      http://localhost:${WEB_PORT}"
        log ""
        log "stop with: ./scripts/dev.sh down"
    fi
}

cmd_down() {
    ensure_dirs

    # Stop process-mode services
    for svc in worker api web; do
        stop_process "${svc}"
    done

    # Stop containers
    for name in "${CONTAINER_SERVICES[@]}"; do
        remove_container "${name}"
    done

    log "all services stopped"
}

cmd_status() {
    ensure_dirs
    local any_running=false

    # Check process-mode services
    for svc in worker api web; do
        if is_running "${svc}"; then
            local pid
            pid="$(read_pid "${svc}")"
            printf '%-10s process  pid=%-8s running\n' "${svc}" "${pid}"
            any_running=true
        fi
    done

    # Check containers
    for name in "${CONTAINER_SERVICES[@]}"; do
        if container_exists "${name}"; then
            local status
            status="$(${CONTAINER_CMD} inspect -f '{{.State.Status}}' "${name}" 2>/dev/null || echo "unknown")"
            printf '%-10s container  %-12s %s\n' "${name#spring-dev-}" "${name}" "${status}"
            any_running=true
        fi
    done

    if [[ "${any_running}" == false ]]; then
        log "no services running"
    fi
}

cmd_logs() {
    local svc="${1:-}"

    # If a container exists for this service, use container logs
    local container_name="spring-dev-${svc}"
    if [[ -n "${svc}" ]] && container_exists "${container_name}"; then
        ${CONTAINER_CMD} logs -f "${container_name}"
        return
    fi

    # Otherwise try the process log file
    if [[ -n "${svc}" ]]; then
        local logfile="${LOG_DIR}/${svc}.log"
        if [[ -f "${logfile}" ]]; then
            tail -f "${logfile}"
        else
            die "no logs found for '${svc}' — is it running?"
        fi
        return
    fi

    # No service specified — show all container logs or list process logs
    local found=false
    for name in "${CONTAINER_SERVICES[@]}"; do
        if container_exists "${name}"; then
            ${CONTAINER_CMD} logs -f "${name}" &
            found=true
        fi
    done
    if [[ "${found}" == true ]]; then
        wait
        return
    fi

    # Fall back to process log files
    for svc in worker api web; do
        local logfile="${LOG_DIR}/${svc}.log"
        if [[ -f "${logfile}" ]]; then
            tail -f "${logfile}" &
            found=true
        fi
    done
    if [[ "${found}" == true ]]; then
        wait
    else
        die "no services running — nothing to show"
    fi
}

cmd_build() {
    require "${CONTAINER_CMD}"
    log "building dev image: ${SPRING_DEV_IMAGE:-localhost/spring-voyage-dev:latest}"
    ${CONTAINER_CMD} build \
        -f "${REPO_ROOT}/deployment/Dockerfile.dev" \
        -t "${SPRING_DEV_IMAGE:-localhost/spring-voyage-dev:latest}" \
        "${REPO_ROOT}"
}

usage() {
    cat <<'EOF'
Spring Voyage — local development orchestrator

Commands:
  up [--process]       Start all services (default: container mode)
  down                 Stop all services
  status               Show what is running
  logs [service]       Follow logs (all if omitted)
  build                Build dev container image

Container mode runs everything in podman containers.
Process mode (--process) runs .NET hosts via dapr run for hot-reload.

Environment variables:
  CONTAINER_CMD        podman (default) or docker
  REDIS_PORT           Redis port (default: 6379)
  WORKER_APP_PORT      Worker app port (default: 5001)
  API_APP_PORT         API app port (default: 5000)
  WEB_PORT             Web dashboard port (default: 3000)
EOF
}

main() {
    local cmd="${1:-}"
    shift || true
    case "${cmd}" in
        up)                  cmd_up "$@" ;;
        down)                cmd_down "$@" ;;
        status)              cmd_status "$@" ;;
        logs)                cmd_logs "$@" ;;
        build)               cmd_build "$@" ;;
        ""|-h|--help|help)   usage ;;
        *)                   usage; exit 2 ;;
    esac
}

main "$@"
