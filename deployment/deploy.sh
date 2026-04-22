#!/usr/bin/env bash
# Spring Voyage — local Podman deployment.
#
# Brings up the full container stack on a shared Podman network (spring-net):
#   spring-postgres, spring-redis,
#   spring-placement, spring-scheduler,            (Dapr control plane)
#   spring-api-dapr, spring-worker-dapr,           (per-app Dapr sidecars)
#   spring-worker, spring-api, spring-web, spring-caddy
#
# In addition to the container stack, deploy.sh delegates to
# `spring-voyage-host.sh` to start/stop the spring-dispatcher service as a
# host process. The dispatcher is no longer containerised in the OSS
# deployment because the rootless Podman socket cannot be reliably
# bind-mounted into a container on macOS arm64 / libkrun (issue #1063);
# moving the dispatcher to the host gives Linux/macOS/Windows a single,
# stable topology and removes the podman CLI dependency from every image.
#
# Usage:
#   ./deploy.sh up              # create network, pull/build, start stack + host services
#   ./deploy.sh down            # stop containers + host services (preserves volumes)
#   ./deploy.sh restart         # down + up
#   ./deploy.sh logs [service]  # follow logs for one or all services
#   ./deploy.sh status          # show container + host-service status
#   ./deploy.sh build           # build Dockerfile + Dockerfile.agent images
#   ./deploy.sh ensure-user-net <uid>  # create per-user bridge network for agent isolation
#
# Environment: reads values from ./spring.env (or $SPRING_ENV_FILE).
# See spring.env.example for all supported variables.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
ENV_FILE="${SPRING_ENV_FILE:-${SCRIPT_DIR}/spring.env}"
# Resolved env file passed to podman --env-file. Podman treats --env-file
# values literally (no shell expansion), so we pre-process the source
# spring.env with envsubst to expand ${VAR} references between keys — e.g.
# a ConnectionStrings__SpringDb value that interpolates ${POSTGRES_DB}.
RESOLVED_ENV_FILE=""

NETWORK_NAME="spring-net"
USER_NETWORK_PREFIX="spring-user-"

SERVICES=(
    spring-postgres
    spring-redis
    spring-placement
    spring-scheduler
    spring-worker-dapr
    spring-api-dapr
    spring-worker
    spring-api
    spring-web
    spring-caddy
    spring-ollama
)

# Wrapper around the host-process service manager. The dispatcher lives
# outside the container stack (issue #1063); deploy.sh delegates to this
# script so the lifecycle is observable and operators can manage it
# directly when they want to bounce the dispatcher in isolation.
HOST_SCRIPT="${SCRIPT_DIR}/spring-voyage-host.sh"

# Path to the file the host script writes after `spring-voyage-host.sh
# start` resolves the bearer token, port, and tenant. Sourced before
# starting any container that needs to talk to the dispatcher so the
# worker (and friends) see the *same* SPRING_DISPATCHER_WORKER_TOKEN
# without it being checked into the repo or hardcoded here. Honors
# SPRING_HOST_STATE_DIR exactly the way the host script does.
DISPATCHER_ENV_FILE="${SPRING_HOST_STATE_DIR:-${HOME}/.spring-voyage/host}/dispatcher.env"

# Source the dispatcher env file written by spring-voyage-host.sh.
# Idempotent — silently skips when the file is missing (e.g. dispatcher
# not yet started). Callers that strictly require the file should check
# its existence themselves and fail loudly.
load_dispatcher_env() {
    if [[ -f "${DISPATCHER_ENV_FILE}" ]]; then
        set -a
        # shellcheck disable=SC1090
        source "${DISPATCHER_ENV_FILE}"
        set +a
    fi
}

log()  { printf '[deploy] %s\n' "$*" >&2; }
die()  { printf '[deploy][error] %s\n' "$*" >&2; exit 1; }

require() {
    command -v "$1" >/dev/null 2>&1 || die "required command '$1' not found on PATH"
}

load_env() {
    if [[ ! -f "${ENV_FILE}" ]]; then
        die "env file not found: ${ENV_FILE} (copy spring.env.example to spring.env and edit)"
    fi
    require envsubst
    # Source the env file for this script's use (e.g., image tags, DEPLOY_HOSTNAME).
    # Values are passed into containers via --env-file, not via the shell environment.
    set -a
    # shellcheck disable=SC1090
    source "${ENV_FILE}"
    set +a

    # Expand ${VAR} references inside the env file itself and write the
    # result to a short-lived file that we pass to podman --env-file.
    # Podman's --env-file reader is literal-only, so a value like
    # `Host=...;Database=${POSTGRES_DB};...` would otherwise be forwarded
    # un-expanded to the container (see #261).
    RESOLVED_ENV_FILE="$(mktemp "${TMPDIR:-/tmp}/spring.env.resolved.XXXXXX")"
    chmod 600 "${RESOLVED_ENV_FILE}"
    envsubst < "${ENV_FILE}" > "${RESOLVED_ENV_FILE}"
    trap 'rm -f "${RESOLVED_ENV_FILE}"' EXIT
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
        --env-file "${RESOLVED_ENV_FILE}" \
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

# ---- Dapr control plane (placement + scheduler) --------------------------
#
# We run our own placement and scheduler on spring-net instead of relying on
# the `dapr init` leftovers (dapr_placement / dapr_scheduler) which live on
# Podman's default network and are invisible to spring-net. This keeps the
# deployment self-contained — `./deploy.sh up` from a fresh host works
# without `dapr init` ever having been run.
#
# Image: the same daprio/dapr release used for the per-app sidecars, so the
# placement/scheduler wire format always matches the sidecar's expectations.

start_placement() {
    # Run with default flags — matches dapr init's dapr_placement.
    # Overriding --id without matching cluster config crashes the binary.
    run_container spring-placement \
        -v spring-placement-data:/var/run/dapr/raft \
        "${DAPR_IMAGE:-docker.io/daprio/dapr:1.17.4}" \
        ./placement
}

start_scheduler() {
    # Mount to /var/lock (writable for the image's non-root user) and
    # override the broadcast host so spring-net peers can reach the
    # scheduler by DNS name. Default id (dapr-scheduler-server-0) keeps
    # the embedded etcd single-node bootstrap happy.
    run_container spring-scheduler \
        -v spring-scheduler-data:/var/lock \
        "${DAPR_IMAGE:-docker.io/daprio/dapr:1.17.4}" \
        ./scheduler \
            --etcd-data-dir=/var/lock/dapr/scheduler \
            --etcd-client-listen-address=0.0.0.0 \
            --override-broadcast-host-port=spring-scheduler:50006
}

# ---- Per-app Dapr sidecars -----------------------------------------------
#
# Each app container (spring-api, spring-worker) is paired with a daprd
# sidecar container on spring-net. The app points at the sidecar via
# DAPR_HTTP_ENDPOINT / DAPR_GRPC_ENDPOINT (honored by the Dapr .NET SDK) so
# the app does not need to share localhost with daprd (issue #308).
#
# Components and config are bind-mounted from the repo so operators can
# tweak them without rebuilding images. Mount as :ro so the sidecar cannot
# accidentally mutate them.

start_api_sidecar() {
    run_container spring-api-dapr \
        --env-file "${RESOLVED_ENV_FILE}" \
        -v "${REPO_ROOT}/dapr/components/production:/components:ro,Z" \
        -v "${REPO_ROOT}/dapr/config/production.yaml:/config/config.yaml:ro,Z" \
        "${DAPR_IMAGE:-docker.io/daprio/dapr:1.17.4}" \
        ./daprd \
            --app-id spring-api \
            --app-port 8080 \
            --app-channel-address spring-api \
            --dapr-http-port 3500 \
            --dapr-grpc-port 50001 \
            --dapr-listen-addresses 0.0.0.0 \
            --resources-path /components \
            --config /config/config.yaml \
            --placement-host-address spring-placement:50005 \
            --scheduler-host-address spring-scheduler:50006 \
            --log-level info \
            --enable-metrics=false
}

start_worker_sidecar() {
    run_container spring-worker-dapr \
        --env-file "${RESOLVED_ENV_FILE}" \
        -v "${REPO_ROOT}/dapr/components/production:/components:ro,Z" \
        -v "${REPO_ROOT}/dapr/config/production.yaml:/config/config.yaml:ro,Z" \
        "${DAPR_IMAGE:-docker.io/daprio/dapr:1.17.4}" \
        ./daprd \
            --app-id spring-worker \
            --app-port 8080 \
            --app-channel-address spring-worker \
            --dapr-http-port 3500 \
            --dapr-grpc-port 50001 \
            --dapr-listen-addresses 0.0.0.0 \
            --resources-path /components \
            --config /config/config.yaml \
            --placement-host-address spring-placement:50005 \
            --scheduler-host-address spring-scheduler:50006 \
            --log-level info \
            --enable-metrics=false
}

start_worker() {
    # DataProtection keys: API and Worker share the named volume
    # `spring-dataprotection-keys` mounted at the path configured via
    # DataProtection__KeysPath (defaults to /home/app/.aspnet/DataProtection-Keys).
    # Keeps the key ring stable across `./deploy.sh restart` and image
    # rebuilds so anything protected by IDataProtector (auth cookies,
    # OAuth session tokens, anti-forgery tokens) survives deploys. See #337.
    #
    # Dispatcher wiring: the worker never holds the podman binary. It reaches
    # spring-dispatcher over HTTP for every container op (#513). The dispatcher
    # itself runs as a host process (#1063), so the worker resolves it via
    # `host.containers.internal` — Podman's stable host-loopback DNS name —
    # rather than a sibling container hostname. The bearer token is an opaque
    # shared secret; see spring.env.example.
    load_dispatcher_env
    local dispatcher_port="${SPRING_DISPATCHER_PORT:-8090}"
    if [[ -z "${SPRING_DISPATCHER_WORKER_TOKEN:-}" ]]; then
        die "SPRING_DISPATCHER_WORKER_TOKEN is not set. The dispatcher must be started first ('${HOST_SCRIPT##${REPO_ROOT}/} start') so it can write the bearer token to ${DISPATCHER_ENV_FILE} for the worker to source."
    fi
    run_container spring-worker \
        --env-file "${RESOLVED_ENV_FILE}" \
        -e "DAPR_APP_ID=spring-worker" \
        -e "DAPR_HTTP_ENDPOINT=http://spring-worker-dapr:3500" \
        -e "DAPR_GRPC_ENDPOINT=http://spring-worker-dapr:50001" \
        -e "Dispatcher__BaseUrl=http://host.containers.internal:${dispatcher_port}/" \
        -e "Dispatcher__BearerToken=${SPRING_DISPATCHER_WORKER_TOKEN}" \
        -v spring-dataprotection-keys:/home/app/.aspnet/DataProtection-Keys \
        "${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}" \
        dotnet /app/Cvoya.Spring.Host.Worker.dll
}

# ---- spring-dispatcher (host process) ------------------------------------
#
# The dispatcher is the only process that holds the host container-runtime
# (podman) credentials. Workers reach it over HTTP for every container op
# — no worker ships podman on its own PATH. See
# docs/architecture/deployment.md and #513.
#
# Since #1063 the dispatcher runs as a long-lived host process owned by
# `spring-voyage-host.sh`. Running on the host removes the rootless
# podman-socket bind-mount entirely (which fails reliably on macOS arm64
# under libkrun) and gives Linux/macOS/Windows a single topology. This
# wrapper exists so the deploy.sh up/down lifecycle is one verb for the
# operator; advanced workflows (bounce dispatcher only, tail dispatcher
# logs without touching the stack) call the host script directly.
start_dispatcher() {
    [[ -x "${HOST_SCRIPT}" ]] || die "host-services script not found at ${HOST_SCRIPT} — run 'chmod +x ${HOST_SCRIPT}'"
    log "starting spring-dispatcher via ${HOST_SCRIPT##${REPO_ROOT}/}"
    "${HOST_SCRIPT}" start
}

stop_dispatcher() {
    [[ -x "${HOST_SCRIPT}" ]] || return 0
    log "stopping spring-dispatcher via ${HOST_SCRIPT##${REPO_ROOT}/}"
    "${HOST_SCRIPT}" stop || true
}

start_api() {
    # DataProtection keys: see start_worker for the rationale (#337).
    run_container spring-api \
        --env-file "${RESOLVED_ENV_FILE}" \
        -e "DAPR_APP_ID=spring-api" \
        -e "DAPR_HTTP_ENDPOINT=http://spring-api-dapr:3500" \
        -e "DAPR_GRPC_ENDPOINT=http://spring-api-dapr:50001" \
        -v spring-dataprotection-keys:/home/app/.aspnet/DataProtection-Keys \
        "${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}" \
        dotnet /app/Cvoya.Spring.Host.Api.dll
}

start_web() {
    run_container spring-web \
        --env-file "${RESOLVED_ENV_FILE}" \
        -e "NEXT_PUBLIC_API_URL=http://spring-api:8080" \
        "${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}" \
        node /app/web/src/Cvoya.Spring.Web/server.js
}

# ---- Ollama (local LLM backend) -----------------------------------------
#
# OLLAMA_MODE selects between the container path (default: "container") and
# the host-installed path ("host"). The host path exists primarily for macOS:
# Metal GPU acceleration does not pass through into Podman containers, so
# operators who want GPU-accelerated local inference install Ollama via
# `brew install ollama` and run `ollama serve` on the host. In that mode the
# platform talks to Ollama over `host.containers.internal:11434` and this
# script does NOT start a container.
#
# OLLAMA_GPU optionally enables GPU passthrough for the container path. Set
# to "nvidia" on Linux/WSL2 with the NVIDIA Container Toolkit installed —
# the script adds `--device nvidia.com/gpu=all`. Default is CPU-only which
# works everywhere.
#
# OLLAMA_DEFAULT_MODEL is pulled into the container on first run (best-
# effort; failures are logged but don't abort the deploy).
start_ollama() {
    local mode="${OLLAMA_MODE:-container}"
    if [[ "${mode}" == "host" ]]; then
        log "OLLAMA_MODE=host — skipping container. Ensure 'ollama serve' is running on the host (port 11434)."
        log "  macOS: brew install ollama && ollama serve"
        log "  Linux: https://ollama.com/download"
        log "Platform talks to it via LanguageModel__Ollama__BaseUrl (default http://host.containers.internal:11434)."
        return
    fi

    local gpu_args=()
    case "${OLLAMA_GPU:-}" in
        nvidia)
            gpu_args+=(--device "nvidia.com/gpu=all")
            log "ollama: enabling NVIDIA GPU passthrough (requires nvidia-container-toolkit on the host)"
            ;;
        "")
            : # CPU-only default
            ;;
        *)
            log "warning: unsupported OLLAMA_GPU='${OLLAMA_GPU}', falling back to CPU"
            ;;
    esac

    run_container spring-ollama \
        -p "${OLLAMA_PORT:-11434}:11434" \
        -v spring-ollama-data:/root/.ollama \
        ${gpu_args[@]+"${gpu_args[@]}"} \
        "${OLLAMA_IMAGE:-docker.io/ollama/ollama:latest}"
}

pull_ollama_default_model() {
    [[ "${OLLAMA_MODE:-container}" == "host" ]] && return 0

    local model="${OLLAMA_DEFAULT_MODEL:-llama3.2:3b}"
    log "pulling Ollama default model '${model}' (best-effort, may take a few minutes)"

    # Poll briefly for the Ollama HTTP API to come up before pulling. Ollama
    # reports ready once it binds :11434.
    local waited=0
    while (( waited < 30 )); do
        if podman exec spring-ollama ollama list >/dev/null 2>&1; then
            break
        fi
        sleep 1
        waited=$(( waited + 1 ))
    done

    if ! podman exec spring-ollama ollama pull "${model}" >/dev/null 2>&1; then
        log "warning: failed to pull Ollama model '${model}'. Pull manually with: podman exec spring-ollama ollama pull ${model}"
        return 0
    fi
    log "pulled Ollama model '${model}'"
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
        --env-file "${RESOLVED_ENV_FILE}" \
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

wait_sidecar_ready() {
    # Polls the daprd HTTP healthz endpoint from a throwaway curl container
    # on spring-net. We can't `podman exec` the sidecar itself: daprio/dapr
    # is effectively distroless (no shell, no wget, no curl — just daprd),
    # so any in-container probe fails with "executable file not found" and
    # the readiness wait silently burns through its full timeout.
    local name="$1" timeout="${2:-30}"
    local waited=0
    local curl_image="${SPRING_CURL_IMAGE:-docker.io/curlimages/curl:latest}"
    log "waiting for Dapr sidecar '${name}' to become ready"
    while (( waited < timeout )); do
        # /v1.0/healthz/outbound reports sidecar-only readiness (components
        # loaded, control-plane reachable). It does NOT require the paired
        # app to be up — which is what we want here, since the apps are
        # started immediately after.
        # --max-time 2 bounds each attempt so a real outage still fires the
        # overall deadline; -sf makes curl fail on non-2xx so the branch is
        # honest about real failures.
        if podman run --rm --network "${NETWORK_NAME}" "${curl_image}" \
                -sf -o /dev/null --max-time 2 \
                "http://${name}:3500/v1.0/healthz/outbound" >/dev/null 2>&1; then
            log "sidecar '${name}' is ready"
            return 0
        fi
        sleep 1
        waited=$(( waited + 1 ))
    done
    log "WARNING: sidecar '${name}' did not report ready within ${timeout}s; continuing anyway"
    return 0
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

    # Ollama starts before the app containers so the platform's startup
    # health check (OllamaHealthCheck) has a reachable target. No --health-cmd
    # is attached because the Ollama image ships without curl/wget — we poll
    # via `ollama list` when pulling the default model instead.
    start_ollama
    pull_ollama_default_model

    # Dapr control plane on spring-net. These must be up before any per-app
    # sidecar tries to register with placement / schedule actor reminders.
    start_placement
    start_scheduler

    # Per-app Dapr sidecars. Start both before the apps so DAPR_HTTP_ENDPOINT
    # / DAPR_GRPC_ENDPOINT resolves the moment the apps come up (#308).
    start_worker_sidecar
    start_api_sidecar
    wait_sidecar_ready spring-worker-dapr 30
    wait_sidecar_ready spring-api-dapr 30

    # Dispatcher must be up before the worker — the worker's only
    # IContainerRuntime binding is a DispatcherClientContainerRuntime that
    # HTTP-calls spring-dispatcher on first use (#513). Since #1063 the
    # dispatcher runs on the host, so this is a host-process start, not a
    # container.
    start_dispatcher

    start_worker
    start_api
    start_web
    start_caddy

    log "stack is up. API: http://${DEPLOY_HOSTNAME:-localhost}  Web: http://${DEPLOY_HOSTNAME:-localhost}/"
}

cmd_down() {
    require podman
    # Stop the host-process dispatcher first so it can finish in-flight
    # podman calls cleanly before the agent containers it owns disappear
    # underneath it.
    stop_dispatcher
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
    if [[ -x "${HOST_SCRIPT}" ]]; then
        echo
        "${HOST_SCRIPT}" status || true
    fi
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

    # spring-dispatcher is a host process (#1063); we publish its .NET binary
    # via spring-voyage-host.sh build instead of producing an image.
    if [[ -x "${HOST_SCRIPT}" ]]; then
        log "publishing spring-dispatcher host binary"
        "${HOST_SCRIPT}" build
    fi

    log "building dapr-agent image: ${SPRING_DAPR_AGENT_IMAGE:-localhost/spring-dapr-agent:latest}"
    podman build \
        -f "${REPO_ROOT}/agents/dapr-agent/Dockerfile" \
        -t "${SPRING_DAPR_AGENT_IMAGE:-localhost/spring-dapr-agent:latest}" \
        "${REPO_ROOT}/agents/dapr-agent"
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
