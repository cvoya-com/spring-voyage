#!/usr/bin/env bash
# Spring Voyage — local Podman deployment.
#
# Brings up the full container stack on a shared Podman network (spring-net):
#   spring-postgres, spring-redis, spring-worker, spring-api, spring-web, spring-caddy
#
# Usage:
#   ./deploy.sh up              # create network, pull/build, start stack
#   ./deploy.sh down            # stop and remove containers (preserves volumes)
#   ./deploy.sh restart         # down + up
#   ./deploy.sh logs [service]  # follow logs for one or all services
#   ./deploy.sh status          # show container status
#   ./deploy.sh build           # build Dockerfile + Dockerfile.agent images
#   ./deploy.sh ensure-user-net <uid>  # create per-user bridge network for agent isolation
#
# Environment: reads values from ./spring.env (or $SPRING_ENV_FILE).
# See spring.env.example for all supported variables.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
ENV_FILE="${SPRING_ENV_FILE:-${SCRIPT_DIR}/spring.env}"

NETWORK_NAME="spring-net"
USER_NETWORK_PREFIX="spring-user-"

SERVICES=(spring-postgres spring-redis spring-worker spring-api spring-web spring-caddy)

log()  { printf '[deploy] %s\n' "$*" >&2; }
die()  { printf '[deploy][error] %s\n' "$*" >&2; exit 1; }

require() {
    command -v "$1" >/dev/null 2>&1 || die "required command '$1' not found on PATH"
}

load_env() {
    if [[ ! -f "${ENV_FILE}" ]]; then
        die "env file not found: ${ENV_FILE} (copy spring.env.example to spring.env and edit)"
    fi
    # Source the env file for this script's use (e.g., image tags, DEPLOY_HOSTNAME).
    # Values are passed into containers via --env-file, not via the shell environment.
    set -a
    # shellcheck disable=SC1090
    source "${ENV_FILE}"
    set +a
}

ensure_network() {
    local net="$1"
    if podman network exists "${net}" 2>/dev/null; then
        log "network '${net}' already exists"
    else
        log "creating network '${net}'"
        podman network create "${net}" >/dev/null
    fi
}

ensure_user_network() {
    local uid="$1"
    [[ -n "${uid}" ]] || die "ensure-user-net requires a user id argument"
    # Per-user bridge network for agent execution container isolation.
    # Agents for user <uid> join ${USER_NETWORK_PREFIX}<uid> so they can reach
    # the shared platform network only through the agent-launcher, not each other.
    local net="${USER_NETWORK_PREFIX}${uid}"
    ensure_network "${net}"
    printf '%s\n' "${net}"
}

container_exists() {
    podman container exists "$1" 2>/dev/null
}

remove_container() {
    local name="$1"
    if container_exists "${name}"; then
        log "removing existing container '${name}'"
        podman rm -f "${name}" >/dev/null
    fi
}

run_container() {
    # Idempotent: remove any existing container with the same name before creating.
    local name="$1"; shift
    remove_container "${name}"
    log "starting '${name}'"
    podman run -d --name "${name}" --network "${NETWORK_NAME}" --restart=unless-stopped "$@" >/dev/null
}

# ---------- service definitions ----------

start_postgres() {
    run_container spring-postgres \
        --env-file "${ENV_FILE}" \
        -v spring-postgres-data:/var/lib/postgresql/data \
        --health-cmd 'pg_isready -U "${POSTGRES_USER}" -d "${POSTGRES_DB}"' \
        --health-interval 10s \
        --health-timeout 5s \
        --health-retries 5 \
        "${POSTGRES_IMAGE:-docker.io/library/postgres:17}"
}

start_redis() {
    local cmd=(redis-server --appendonly yes)
    if [[ -n "${REDIS_PASSWORD:-}" ]]; then
        cmd+=(--requirepass "${REDIS_PASSWORD}")
    fi
    run_container spring-redis \
        -v spring-redis-data:/data \
        --health-cmd 'redis-cli ping | grep -q PONG' \
        --health-interval 10s \
        --health-timeout 5s \
        --health-retries 5 \
        "${REDIS_IMAGE:-docker.io/library/redis:7}" \
        "${cmd[@]}"
}

start_worker() {
    run_container spring-worker \
        --env-file "${ENV_FILE}" \
        -e "DAPR_APP_ID=spring-worker" \
        "${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}" \
        dotnet /app/Cvoya.Spring.Host.Worker.dll
}

start_api() {
    run_container spring-api \
        --env-file "${ENV_FILE}" \
        -e "DAPR_APP_ID=spring-api" \
        "${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}" \
        dotnet /app/Cvoya.Spring.Host.Api.dll
}

start_web() {
    run_container spring-web \
        --env-file "${ENV_FILE}" \
        -e "NEXT_PUBLIC_API_URL=http://spring-api:8080" \
        "${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}" \
        node /app/web/server.js
}

start_caddy() {
    # SPRING_CADDYFILE selects which Caddyfile variant to mount. Default is
    # the single-host path-routed "Caddyfile"; set to "Caddyfile.multi-host"
    # for per-service hostnames. Absolute paths are also accepted.
    local caddyfile_name="${SPRING_CADDYFILE:-Caddyfile}"
    local caddyfile
    if [[ "${caddyfile_name}" == /* ]]; then
        caddyfile="${caddyfile_name}"
    else
        caddyfile="${SCRIPT_DIR}/${caddyfile_name}"
    fi
    if [[ ! -f "${caddyfile}" ]]; then
        die "Caddyfile not found at ${caddyfile}"
    fi
    log "using Caddyfile: ${caddyfile}"
    run_container spring-caddy \
        --env-file "${ENV_FILE}" \
        -p 80:80 -p 443:443 \
        -v "${caddyfile}:/etc/caddy/Caddyfile:ro,Z" \
        -v spring-caddy-data:/data \
        -v spring-caddy-config:/config \
        "${CADDY_IMAGE:-docker.io/library/caddy:2}"
}

wait_healthy() {
    # Best-effort wait: skip if health checks aren't configured on the image.
    local name="$1" timeout="${2:-60}"
    local waited=0
    while (( waited < timeout )); do
        local status
        status="$(podman inspect -f '{{.State.Health.Status}}' "${name}" 2>/dev/null || echo "")"
        case "${status}" in
            healthy) return 0 ;;
            unhealthy) die "${name} reported unhealthy" ;;
            "") return 0 ;;   # no healthcheck configured
        esac
        sleep 2
        waited=$(( waited + 2 ))
    done
    die "${name} did not become healthy within ${timeout}s"
}

# ---------- commands ----------

cmd_up() {
    require podman
    load_env
    ensure_network "${NETWORK_NAME}"

    start_postgres
    wait_healthy spring-postgres 60
    start_redis
    wait_healthy spring-redis 30

    start_worker
    start_api
    start_web
    start_caddy

    log "stack is up. API: http://${DEPLOY_HOSTNAME:-localhost}  Web: http://${DEPLOY_HOSTNAME:-localhost}/"
}

cmd_down() {
    require podman
    for svc in "${SERVICES[@]}"; do
        remove_container "${svc}"
    done
    log "stack is down (volumes preserved)"
}

cmd_restart() {
    cmd_down
    cmd_up
}

cmd_status() {
    require podman
    podman ps --filter "name=spring-" --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
}

cmd_logs() {
    require podman
    local svc="${1:-}"
    if [[ -n "${svc}" ]]; then
        podman logs -f "${svc}"
    else
        podman logs -f "${SERVICES[@]}"
    fi
}

cmd_build() {
    require podman
    load_env
    log "building platform image: ${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}"
    podman build \
        -f "${SCRIPT_DIR}/Dockerfile" \
        -t "${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}" \
        "${REPO_ROOT}"

    log "building agent image: ${SPRING_AGENT_IMAGE:-localhost/spring-voyage-agent:latest}"
    podman build \
        -f "${SCRIPT_DIR}/Dockerfile.agent" \
        -t "${SPRING_AGENT_IMAGE:-localhost/spring-voyage-agent:latest}" \
        "${REPO_ROOT}"
}

cmd_ensure_user_net() {
    require podman
    ensure_user_network "${1:-}"
}

usage() {
    cat <<EOF
Spring Voyage — Podman deployment

Commands:
  up                     Start the full stack on ${NETWORK_NAME}
  down                   Stop and remove containers (keeps volumes)
  restart                down + up
  status                 Show container status
  logs [service]         Follow logs (all services if omitted)
  build                  Build Dockerfile + Dockerfile.agent images
  ensure-user-net <uid>  Create per-user bridge network for agent isolation

Environment file: ${ENV_FILE}
  Override with SPRING_ENV_FILE=/path/to/other.env
EOF
}

main() {
    local cmd="${1:-}"
    shift || true
    case "${cmd}" in
        up)                  cmd_up "$@" ;;
        down)                cmd_down "$@" ;;
        restart)             cmd_restart "$@" ;;
        status)              cmd_status "$@" ;;
        logs)                cmd_logs "$@" ;;
        build)               cmd_build "$@" ;;
        ensure-user-net)     cmd_ensure_user_net "$@" ;;
        ""|-h|--help|help)   usage ;;
        *)                   usage; exit 2 ;;
    esac
}

main "$@"
