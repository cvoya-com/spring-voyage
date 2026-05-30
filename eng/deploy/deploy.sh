#!/usr/bin/env bash
# Spring Voyage — local Podman deployment.
#
# Brings up the full container stack on two Podman networks:
#   spring-net               — platform services (api, worker, web, db, dapr)
#   spring-tenant-default    — per-tenant bridge for agent containers
#                              (ADR 0028 — Decision A, issue #1160)
#
# Containers on spring-net:
#   spring-postgres, spring-redis,
#   spring-placement, spring-scheduler,            (Dapr control plane)
#   spring-api-dapr, spring-worker-dapr,           (per-app Dapr sidecars)
#   spring-worker, spring-api, spring-web, spring-caddy
#
# Containers on spring-tenant-default:
#   spring-caddy (also on spring-net — dual-attached so agent/workflow
#                 containers resolve `spring-caddy:8443` from inside the
#                 tenant namespace — ADR 0028 Decision D, issue #1169)
#   spring-ollama (also on spring-net — dual-attached so agents can resolve
#                  `spring-ollama:11434` from inside the tenant namespace)
#   …plus ephemeral / persistent agent containers launched at dispatch time.
#
# In addition to the container stack, deploy.sh delegates to
# `spring-voyage-host.sh` to rebuild/start/stop the spring-dispatcher service
# as a host process. The dispatcher is no longer containerised in the OSS
# deployment because the rootless Podman socket cannot be reliably
# bind-mounted into a container on macOS arm64 / libkrun (issue #1063);
# moving the dispatcher to the host gives Linux/macOS/Windows a single,
# stable topology and removes the podman CLI dependency from every image.
#
# Usage:
#   ./deploy.sh up              # create network, start stack + host services
#   ./deploy.sh up --local-ollama [--ollama-endpoint <host-or-url>] [--ollama-port <port>]
#   ./deploy.sh down            # stop containers + host services (preserves volumes)
#   ./deploy.sh clean           # stop containers, remove volumes/networks/images
#   ./deploy.sh restart         # down + up
#   ./deploy.sh logs [service]  # follow logs for one or all services
#   ./deploy.sh status          # show container + host-service status
#   ./deploy.sh ensure-user-net <uid>  # create per-user bridge network for agent isolation
#
# Environment: reads values from eng/config/spring.env (or $SPRING_ENV_FILE).
# See eng/config/spring.env.example for all supported variables.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
# Bundle mode: manifest.json lives at the bundle root (SCRIPT_DIR).
# Adjust REPO_ROOT so that dapr/, config/, and other bundle-relative paths
# resolve correctly when running from a deployed install.
if [[ -f "${SCRIPT_DIR}/manifest.json" ]]; then
  REPO_ROOT="${SCRIPT_DIR}"
fi
CONFIG_DIR="${REPO_ROOT}/eng/config"
# Bundle mode: spring.env.example (and the env file default) live at the bundle root.
if [[ -f "${SCRIPT_DIR}/manifest.json" && ! -d "${CONFIG_DIR}" ]]; then
  CONFIG_DIR="${SCRIPT_DIR}"
fi
ENV_FILE="${SPRING_ENV_FILE:-${CONFIG_DIR}/spring.env}"
# Resolved env file passed to podman --env-file. Podman treats --env-file
# values literally (no shell expansion), so we pre-process the source
# spring.env with envsubst to expand ${VAR} references between keys — e.g.
# a ConnectionStrings__SpringDb value that interpolates ${POSTGRES_DB}.
RESOLVED_ENV_FILE=""

NETWORK_NAME="spring-net"
USER_NETWORK_PREFIX="spring-user-"
# Per-tenant bridge agent containers attach to (ADR 0028 — Decision A,
# issue #1160). OSS is single-tenant so we ship one network here; the
# cloud overlay creates one per tenant and the dispatcher resolves the
# right one by tenant id. Ollama is dual-attached to this network in
# start_ollama so agents can reach it from inside the tenant namespace
# without crossing onto spring-net.
TENANT_NETWORK_NAME="spring-tenant-default"

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

VOLUMES=(
    spring-postgres-data
    spring-redis-data
    spring-placement-data
    spring-scheduler-data
    spring-dataprotection-keys
    spring-ollama-data
    spring-caddy-data
    spring-caddy-config
)

# Runtime containers launched through the platform dispatcher. These are
# created after deploy.sh brings the stack up, so they are not listed in
# SERVICES, but they are still owned by this local deployment.
RUNTIME_CONTAINER_NAME_PATTERN='^spring-(persistent|ephemeral|exec|dapr)-'

# Wrapper around the host-process service manager. The dispatcher lives
# outside the container stack (issue #1063); deploy.sh delegates to this
# script so the lifecycle is observable and operators can manage it
# directly when they want to bounce the dispatcher in isolation.
HOST_SCRIPT="${SCRIPT_DIR}/spring-voyage-host.sh"
BUILD_SCRIPT="${SCRIPT_DIR}/build.sh"
# Resyncs the podman-machine VM clock onto NTP time. On macOS/Windows the VM
# clock freezes during host sleep and falls behind real time; a skewed clock
# makes the GitHub connector sign already-expired App JWTs (#2595). `up` runs
# this best-effort so a post-sleep deploy starts with an honest clock.
CLOCK_SCRIPT="${SCRIPT_DIR}/resync-container-clock.sh"

# Path to the file the host script writes after `spring-voyage-host.sh
# start` resolves the bearer token, port, and tenant. Sourced before
# starting any container that needs to talk to the dispatcher so the
# worker (and friends) see the *same* SPRING_DISPATCHER_WORKER_TOKEN
# without it being checked into the repo or hardcoded here. Honors
# SPRING_HOST_STATE_DIR exactly the way the host script does.
DISPATCHER_ENV_FILE="${SPRING_HOST_STATE_DIR:-${HOME}/.spring-voyage/host}/dispatcher.env"
DEPLOY_STATE_DIR="${SPRING_DEPLOY_STATE_DIR:-${HOME}/.spring-voyage/deployment}"
LOCAL_OLLAMA_COMPONENTS_DIR="${DEPLOY_STATE_DIR}/dapr/components/delegated-spring-voyage-agent-local-ollama"

# Production Dapr components/config: repo layout uses eng/dapr/...; bundle layout uses dapr/...
DAPR_PRODUCTION_COMPONENTS_DIR="${REPO_ROOT}/eng/dapr/components/production"
if [[ ! -d "${DAPR_PRODUCTION_COMPONENTS_DIR}" && -d "${REPO_ROOT}/dapr/components/production" ]]; then
  DAPR_PRODUCTION_COMPONENTS_DIR="${REPO_ROOT}/dapr/components/production"
fi
DAPR_PRODUCTION_CONFIG_FILE="${REPO_ROOT}/eng/dapr/config/production.yaml"
if [[ ! -f "${DAPR_PRODUCTION_CONFIG_FILE}" && -f "${REPO_ROOT}/dapr/config/production.yaml" ]]; then
  DAPR_PRODUCTION_CONFIG_FILE="${REPO_ROOT}/dapr/config/production.yaml"
fi

LOCAL_OLLAMA_REQUESTED=0
LOCAL_OLLAMA_ENDPOINT=""
LOCAL_OLLAMA_PORT=""

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

# 0 = a process is listening on the port, 1 = free, 2 = could not determine.
# Prefer ss: it lists every listener from the kernel table regardless of owner,
# so a non-root run still sees root-owned listeners (e.g. a system reverse proxy
# on 80/443). lsof as a non-root user cannot see other users' sockets.
port_in_use() {
    local port="$1"
    if command -v ss >/dev/null 2>&1; then
        ss -lnt 2>/dev/null | awk '{print $4}' | grep -Eq "[:.]${port}$" && return 0 || return 1
    elif command -v lsof >/dev/null 2>&1; then
        lsof -nP -iTCP:"${port}" -sTCP:LISTEN >/dev/null 2>&1 && return 0 || return 1
    elif command -v netstat >/dev/null 2>&1; then
        netstat -an 2>/dev/null | awk '{print $4}' | grep -Eq "[:.]${port}$" && return 0 || return 1
    fi
    return 2
}

# Echo the kernel's unprivileged-port floor (the rootless bind floor): a
# rootless process cannot bind below it. /proc, then sysctl, then the default.
unprivileged_port_floor() {
    local v
    v="$(cat /proc/sys/net/ipv4/ip_unprivileged_port_start 2>/dev/null \
         || sysctl -n net.ipv4.ip_unprivileged_port_start 2>/dev/null || echo 1024)"
    [[ "$v" =~ ^[0-9]+$ ]] && printf '%s' "$v" || printf '1024'
}

load_env() {
    if [[ ! -f "${ENV_FILE}" ]]; then
        die "env file not found: ${ENV_FILE}. Run '${BASH_SOURCE[0]##*/} init' to generate one (creates spring.env from the example template and provisions a fresh AES-256 secrets key)."
    fi
    require envsubst
    # Source the env file for this script's use (e.g., image tags, DEPLOY_HOSTNAME).
    # Values are passed into containers via --env-file, not via the shell environment.
    set -a
    # shellcheck disable=SC1090
    source "${ENV_FILE}"
    set +a

    # Tier-1 secrets-key gate: SPRING_SECRETS_AES_KEY (or Secrets__AesKeyFile)
    # is now mandatory; the previous AllowEphemeralDevKey fallback was
    # removed because a per-process random key cannot work in the platform's
    # multi-process topology. Catch the placeholder + the unset case here so
    # operators see a precise message at `deploy.sh up` rather than a
    # cryptic boot-time validator throw inside spring-api.
    if [[ -z "${SPRING_SECRETS_AES_KEY:-}" && -z "${Secrets__AesKeyFile:-}" ]]; then
        die "SPRING_SECRETS_AES_KEY is not set in ${ENV_FILE}. Run '${BASH_SOURCE[0]##*/} init' to generate one, or set Secrets__AesKeyFile to a mounted key file."
    fi
    if [[ "${SPRING_SECRETS_AES_KEY:-}" == "REPLACE_ME_WITH_BASE64_32_BYTES" ]]; then
        die "SPRING_SECRETS_AES_KEY in ${ENV_FILE} still carries the placeholder value. Run '${BASH_SOURCE[0]##*/} init' to substitute a freshly-generated key."
    fi

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

set_resolved_env_var() {
    local key="$1"
    local value="$2"
    [[ -n "${RESOLVED_ENV_FILE}" ]] || die "resolved env file is not initialized"

    local tmp
    tmp="$(mktemp "${RESOLVED_ENV_FILE}.XXXXXX")"
    awk -v key="${key}" -v value="${value}" '
        BEGIN { replaced = 0 }
        index($0, key "=") == 1 {
            print key "=" value
            replaced = 1
            next
        }
        { print }
        END {
            if (!replaced) {
                print key "=" value
            }
        }
    ' "${RESOLVED_ENV_FILE}" > "${tmp}"
    chmod 600 "${tmp}"
    mv "${tmp}" "${RESOLVED_ENV_FILE}"

    declare -gx "${key}=${value}"
}

validate_port() {
    local value="$1"
    [[ "${value}" =~ ^[0-9]+$ ]] || die "port must be numeric: ${value}"
    local numeric_value=$((10#${value}))
    (( numeric_value >= 1 && numeric_value <= 65535 )) || die "port must be between 1 and 65535: ${value}"
}

normalize_ollama_base_url() {
    local endpoint="$1"
    local port="$2"
    local explicit_port="$3"

    endpoint="${endpoint%/}"
    [[ -n "${endpoint}" ]] || die "--ollama-endpoint cannot be empty"
    if [[ "${endpoint}" != *"://"* ]]; then
        endpoint="http://${endpoint}"
    fi

    local rest="${endpoint#*://}"
    if [[ "${rest}" == */* ]]; then
        die "--ollama-endpoint must be a scheme/host[:port] base URL, not include a path: ${endpoint}"
    fi

    local authority="${rest}"
    if [[ "${authority}" =~ :[0-9]+$ ]]; then
        [[ "${explicit_port}" != "1" ]] || die "--ollama-endpoint already includes a port; omit --ollama-port"
        printf '%s\n' "${endpoint}"
        return 0
    fi

    printf '%s:%s\n' "${endpoint}" "${port}"
}

ollama_probe_base_url() {
    local base_url="$1"
    local scheme="${base_url%%://*}"
    local rest="${base_url#*://}"
    local authority="${rest%%/*}"
    local host="${authority%:*}"
    local port="${authority##*:}"

    if [[ "${host}" == "${port}" ]]; then
        port="11434"
    fi
    case "${host}" in
        host.containers.internal|host.docker.internal)
            host="127.0.0.1"
            ;;
    esac

    printf '%s://%s:%s\n' "${scheme}" "${host}" "${port}"
}

check_ollama_endpoint() {
    local probe_base_url="$1"
    require curl

    log "checking host-installed Ollama at ${probe_base_url}"
    if curl -fsS --max-time 5 "${probe_base_url}/api/tags" >/dev/null; then
        log "verified host-installed Ollama endpoint"
        return 0
    fi

    if [[ "${probe_base_url}" == "http://127.0.0.1:11434" ]] && ! command -v ollama >/dev/null 2>&1; then
        die "host-installed Ollama was requested, but the ollama CLI is not on PATH and ${probe_base_url}/api/tags is not reachable. Install Ollama and start it with 'ollama serve', or pass --ollama-endpoint/--ollama-port for a reachable service."
    fi

    die "host-installed Ollama was requested, but ${probe_base_url}/api/tags is not reachable. Start Ollama, or pass --ollama-endpoint/--ollama-port for the reachable service."
}

configure_local_ollama() {
    local mode="${OLLAMA_MODE:-container}"
    if [[ "${LOCAL_OLLAMA_REQUESTED}" -eq 1 ]]; then
        mode="host"
    fi
    [[ "${mode}" == "host" ]] || return 0

    local base_url
    if [[ "${LOCAL_OLLAMA_REQUESTED}" -eq 1 ]]; then
        local endpoint="${LOCAL_OLLAMA_ENDPOINT:-host.containers.internal}"
        local port="${LOCAL_OLLAMA_PORT:-11434}"
        local explicit_port=0
        if [[ -n "${LOCAL_OLLAMA_PORT}" ]]; then
            explicit_port=1
            validate_port "${LOCAL_OLLAMA_PORT}"
        fi

        base_url="$(normalize_ollama_base_url "${endpoint}" "${port}" "${explicit_port}")"
    else
        local configured_base_url="${LanguageModel__Ollama__BaseUrl:-}"
        if [[ -z "${configured_base_url}" || "${configured_base_url}" == "http://spring-ollama:11434" ]]; then
            configured_base_url="http://host.containers.internal:11434"
        fi
        base_url="$(normalize_ollama_base_url "${configured_base_url}" "11434" "0")"
    fi

    local probe_base_url
    probe_base_url="$(ollama_probe_base_url "${base_url}")"
    check_ollama_endpoint "${probe_base_url}"

    set_resolved_env_var OLLAMA_MODE host
    set_resolved_env_var LanguageModel__Ollama__Enabled true
    set_resolved_env_var LanguageModel__Ollama__BaseUrl "${base_url}"
    set_resolved_env_var AgentContext__LlmProviderUrl "${base_url}"
    configure_local_ollama_dapr_profile "${base_url}"

    log "using host-installed Ollama for platform and agent containers: ${base_url}"
}

ollama_openai_endpoint_url() {
    local base_url="${1%/}"
    if [[ "${base_url}" == */v1 ]]; then
        printf '%s\n' "${base_url}"
    else
        printf '%s/v1\n' "${base_url}"
    fi
}

configure_local_ollama_dapr_profile() {
    local base_url="$1"
    local endpoint
    endpoint="$(ollama_openai_endpoint_url "${base_url}")"

    # Resolve the components source. In the repo layout this lives under
    # eng/dapr/components/...; in the source-free bundle layout (where
    # REPO_ROOT is the bundle root) it lives under dapr/components/...
    local default_source_dir="${REPO_ROOT}/eng/dapr/components/delegated-spring-voyage-agent"
    if [[ ! -d "${default_source_dir}" && -d "${REPO_ROOT}/dapr/components/delegated-spring-voyage-agent" ]]; then
        default_source_dir="${REPO_ROOT}/dapr/components/delegated-spring-voyage-agent"
    fi
    local source_dir="${Dapr__Sidecar__DelegatedSpringVoyageAgentComponentsPath:-${default_source_dir}}"
    if [[ "${source_dir}" == "${LOCAL_OLLAMA_COMPONENTS_DIR}" ]]; then
        source_dir="${default_source_dir}"
    fi
    if [[ ! -d "${source_dir}" && -d "${default_source_dir}" ]]; then
        log "warning: configured delegated Dapr components path does not exist (${source_dir}); using repo path"
        source_dir="${default_source_dir}"
    fi
    [[ -d "${source_dir}" ]] || die "delegated Dapr components directory not found: ${source_dir}"

    rm -rf "${LOCAL_OLLAMA_COMPONENTS_DIR}"
    mkdir -p "$(dirname "${LOCAL_OLLAMA_COMPONENTS_DIR}")"
    cp -R "${source_dir}" "${LOCAL_OLLAMA_COMPONENTS_DIR}"

    local rewrote=0
    while IFS= read -r component_file; do
        rewrite_ollama_component_endpoint "${component_file}" "${endpoint}"
        rewrote=$(( rewrote + 1 ))
    done < <(find "${LOCAL_OLLAMA_COMPONENTS_DIR}" -name 'llm-ollama.yaml' -type f)
    (( rewrote > 0 )) || die "no llm-ollama.yaml files found under ${LOCAL_OLLAMA_COMPONENTS_DIR}"
    chmod -R a+rX "${LOCAL_OLLAMA_COMPONENTS_DIR}"

    set_resolved_env_var Dapr__Sidecar__DelegatedSpringVoyageAgentComponentsPath "${LOCAL_OLLAMA_COMPONENTS_DIR}"
    log "generated local Ollama Dapr profile: ${LOCAL_OLLAMA_COMPONENTS_DIR} (endpoint ${endpoint})"
}

rewrite_ollama_component_endpoint() {
    local component_file="$1"
    local endpoint="$2"
    local tmp
    tmp="$(mktemp "${component_file}.XXXXXX")"
    awk -v endpoint="${endpoint}" '
        /^[[:space:]]*-[[:space:]]name:[[:space:]]endpoint[[:space:]]*$/ {
            print
            in_endpoint = 1
            next
        }
        in_endpoint && /^[[:space:]]*value:[[:space:]]/ {
            print "    value: \"" endpoint "\""
            in_endpoint = 0
            next
        }
        {
            print
            if ($0 ~ /^[[:space:]]*-[[:space:]]name:[[:space:]]/) {
                in_endpoint = 0
            }
        }
    ' "${component_file}" > "${tmp}"
    mv "${tmp}" "${component_file}"
    chmod 0644 "${component_file}"
}

parse_up_options() {
    LOCAL_OLLAMA_REQUESTED=0
    LOCAL_OLLAMA_ENDPOINT=""
    LOCAL_OLLAMA_PORT=""

    while [[ $# -gt 0 ]]; do
        case "$1" in
            --local-ollama)
                LOCAL_OLLAMA_REQUESTED=1
                shift
                ;;
            --local-ollama=*)
                LOCAL_OLLAMA_REQUESTED=1
                LOCAL_OLLAMA_ENDPOINT="${1#*=}"
                shift
                ;;
            --ollama-endpoint)
                [[ $# -ge 2 ]] || die "--ollama-endpoint requires a value"
                LOCAL_OLLAMA_REQUESTED=1
                LOCAL_OLLAMA_ENDPOINT="$2"
                shift 2
                ;;
            --ollama-endpoint=*)
                LOCAL_OLLAMA_REQUESTED=1
                LOCAL_OLLAMA_ENDPOINT="${1#*=}"
                shift
                ;;
            --ollama-port)
                [[ $# -ge 2 ]] || die "--ollama-port requires a value"
                LOCAL_OLLAMA_REQUESTED=1
                LOCAL_OLLAMA_PORT="$2"
                shift 2
                ;;
            --ollama-port=*)
                LOCAL_OLLAMA_REQUESTED=1
                LOCAL_OLLAMA_PORT="${1#*=}"
                shift
                ;;
            -h|--help)
                usage
                exit 0
                ;;
            *)
                die "unknown up option: $1"
                ;;
        esac
    done
}

# Provisions a fresh deployment env file. Idempotent against an empty start
# (no spring.env yet) but refuses to overwrite an existing key once
# substituted — that key is the only thing that can decrypt the secrets in
# the postgres state store, and silently rotating it would orphan every
# stored secret.
cmd_init() {
    require openssl
    local example_file="${CONFIG_DIR}/spring.env.example"
    if [[ ! -f "${example_file}" ]]; then
        die "spring.env.example not found at ${example_file}; cannot bootstrap."
    fi

    if [[ -f "${ENV_FILE}" ]]; then
        # Honour an in-place key already substituted by a previous init or
        # set by the operator. Refuse to clobber it.
        local current_key
        current_key="$(awk -F= '/^SPRING_SECRETS_AES_KEY=/ { sub(/^SPRING_SECRETS_AES_KEY=/, ""); print; exit }' "${ENV_FILE}" || true)"
        if [[ -n "${current_key}" && "${current_key}" != "REPLACE_ME_WITH_BASE64_32_BYTES" ]]; then
            die "SPRING_SECRETS_AES_KEY is already set in ${ENV_FILE}. Refusing to overwrite — rotating it would orphan every encrypted secret in the state store. To rotate intentionally, see docs/developer/secret-store.md (key rotation requires re-encrypting existing values)."
        fi
        log "reusing existing ${ENV_FILE} (no SPRING_SECRETS_AES_KEY yet, or placeholder still present)"
    else
        log "copying ${example_file} -> ${ENV_FILE}"
        cp "${example_file}" "${ENV_FILE}"
    fi

    chmod 0600 "${ENV_FILE}"

    local generated_key
    generated_key="$(openssl rand -base64 32)"

    if grep -q '^SPRING_SECRETS_AES_KEY=' "${ENV_FILE}"; then
        # In-place rewrite of the existing line. Use a temp file so a
        # signal mid-write can't leave a half-rewritten env on disk.
        local tmp
        tmp="$(mktemp "${ENV_FILE}.XXXXXX")"
        awk -v key="${generated_key}" '
            BEGIN { replaced = 0 }
            /^SPRING_SECRETS_AES_KEY=/ {
                print "SPRING_SECRETS_AES_KEY=" key
                replaced = 1
                next
            }
            { print }
            END { if (!replaced) print "SPRING_SECRETS_AES_KEY=" key }
        ' "${ENV_FILE}" > "${tmp}"
        chmod 0600 "${tmp}"
        mv "${tmp}" "${ENV_FILE}"
    else
        printf '\nSPRING_SECRETS_AES_KEY=%s\n' "${generated_key}" >> "${ENV_FILE}"
    fi

    log "provisioned SPRING_SECRETS_AES_KEY in ${ENV_FILE} (mode 0600)"
    log ""
    log "  IMPORTANT: ${ENV_FILE} now contains the only key that can decrypt"
    log "  any secret stored in this deployment's state. Back it up alongside"
    log "  your postgres volume; deleting it permanently orphans every"
    log "  encrypted secret. To rotate intentionally, see"
    log "  docs/developer/secret-store.md (rotation requires re-encrypting"
    log "  existing values, not just regenerating the env entry)."
    log ""
    log "Next steps:"
    log "  1. Edit ${ENV_FILE} to fill in deployment-specific values"
    log "     (DEPLOY_HOSTNAME, GitHub__*, runtime credentials, …)"
    log "  2. ${BASH_SOURCE[0]##*/} up"
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

# Dual-attach Dapr state + control plane so daprd sidecars for delegated
# Python agents (per-launch spring-net-* bridge, second-attach to tenant) can
# resolve these hosts — ADR 0028 "V2 interim" stack (see also docker-compose).
ensure_delegated_dapr_tenant_attachments() {
    for c in spring-postgres spring-redis spring-placement spring-scheduler; do
        ensure_tenant_network_attachment "${c}" "${TENANT_NETWORK_NAME}"
    done
}

ensure_tenant_network_attachment() {
    # Dual-attach a platform-side container to the tenant network so its
    # services resolve from inside the tenant namespace too (ADR 0028 —
    # Decision C in spirit: Ollama is the first dual-attached pivot;
    # Decision E and #1167 will cover the host MCP server next).
    # Idempotent: podman network connect emits a non-zero exit when the
    # container is already on the network — we swallow that case so a
    # repeated `./deploy.sh up` is safe.
    local container="$1"
    local net="${2:-${TENANT_NETWORK_NAME}}"
    if podman network inspect "${net}" --format '{{range .Containers}}{{.Name}} {{end}}' 2>/dev/null \
        | tr ' ' '\n' | grep -qx "${container}"; then
        log "container '${container}' already attached to network '${net}'"
        return 0
    fi
    if podman network connect "${net}" "${container}" >/dev/null 2>&1; then
        log "attached '${container}' to network '${net}'"
    else
        log "warning: failed to attach '${container}' to network '${net}' — agents may not reach it via DNS"
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

remove_volume() {
    local name="$1"
    if podman volume exists "${name}" 2>/dev/null; then
        log "removing volume '${name}'"
        if ! podman volume rm -f "${name}" >/dev/null 2>&1; then
            log "warning: failed to remove volume '${name}'"
        fi
    fi
}

remove_network() {
    local name="$1"
    if podman network exists "${name}" 2>/dev/null; then
        log "removing network '${name}'"
        if ! podman network rm "${name}" >/dev/null 2>&1; then
            log "warning: failed to remove network '${name}' — it may still have non-deploy containers attached"
        fi
    fi
}

remove_generated_local_ollama_profile() {
    if [[ -d "${LOCAL_OLLAMA_COMPONENTS_DIR}" ]]; then
        log "removing generated local Ollama Dapr profile '${LOCAL_OLLAMA_COMPONENTS_DIR}'"
        rm -rf "${LOCAL_OLLAMA_COMPONENTS_DIR}"
    fi
}

owned_user_networks() {
    podman network ls --format '{{.Name}}' 2>/dev/null | grep -E "^${USER_NETWORK_PREFIX}" || true
}

owned_runtime_containers() {
    podman ps -a --format '{{.Names}}' 2>/dev/null | grep -E "${RUNTIME_CONTAINER_NAME_PATTERN}" || true
}

owned_runtime_networks() {
    podman network ls --format '{{.Name}}' 2>/dev/null | grep -E '^spring-net-[[:xdigit:]]+' || true
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
    # shellcheck disable=SC2016
    run_container spring-postgres \
        --env-file "${RESOLVED_ENV_FILE}" \
        -v spring-postgres-data:/var/lib/postgresql/data \
        --health-cmd 'pg_isready -U "${POSTGRES_USER}" -d "${POSTGRES_DB}"' \
        --health-interval 10s \
        --health-timeout 5s \
        --health-retries 5 \
        "${POSTGRES_IMAGE:-docker.io/library/postgres:17}"
}

verify_postgres_password() {
    # Postgres sets its password only on the FIRST init of an empty data volume.
    # If a stale spring-postgres-data volume survives a previous install while
    # spring.env's POSTGRES_PASSWORD was regenerated, every *network* client
    # (worker, api, the Dapr postgres components) fails scram auth with 28P01 and
    # crash-loops — surfacing only as "spring-worker did not become healthy" ~180s
    # later, with the real cause (28P01) buried in container logs. Catch it here,
    # in seconds, with an actionable message.
    #
    # The probe MUST use the network path (host=spring-postgres): pg_hba grants
    # local/127.0.0.1 'trust' (no password check), so a loopback probe passes even
    # with the wrong password — the same trap that hides the mismatch from the
    # local psql in repair_dapr_state_schema_drift below.
    local user="${POSTGRES_USER:-spring}" db="${POSTGRES_DB:-spring}" i
    for i in 1 2 3; do
        if podman exec -e PGPASSWORD="${POSTGRES_PASSWORD:-}" spring-postgres \
               psql -h spring-postgres -U "${user}" -d "${db}" -tAc 'select 1' >/dev/null 2>&1; then
            return 0
        fi
        if (( i < 3 )); then sleep 1; fi
    done
    die "postgres rejected the configured POSTGRES_PASSWORD over the network (scram auth / 28P01).
  The 'spring-postgres-data' volume was initialized with a different password — almost
  certainly a stale volume left by a previous install whose POSTGRES_PASSWORD differed
  (postgres only honors POSTGRES_PASSWORD on the first init of an empty data volume).
  For a clean install, wipe the volume and retry:
      deploy.sh clean        # removes containers + volumes (or: voyage uninstall --purge)
      deploy.sh up
  To keep the existing database instead, restore its original POSTGRES_PASSWORD in
  ${ENV_FILE} so it matches the volume."
}

repair_dapr_state_schema_drift() {
    # Dapr's Postgres component defaults tableName/metadataTableName to
    # unqualified names. Because this deployment connects as user "spring",
    # Postgres resolves unqualified names through "$user",public; before EF
    # creates the spring schema that means public.state, after EF creates it
    # that means spring.state. The component is now pinned to public.*, and
    # this repair merges any rows accidentally written under spring.* back
    # into the canonical tables before sidecars start.
    local db="${POSTGRES_DB:-spring}"
    local user="${POSTGRES_USER:-spring}"

    log "checking Dapr Postgres state table placement"
    podman exec -i spring-postgres psql -v ON_ERROR_STOP=1 -U "${user}" -d "${db}" <<'SQL'
DO $$
BEGIN
    IF to_regclass('spring.state') IS NOT NULL THEN
        IF to_regclass('public.state') IS NULL THEN
            EXECUTE 'CREATE TABLE public.state (LIKE spring.state INCLUDING ALL)';
        END IF;

        INSERT INTO public.state (key, value, isbinary, insertdate, updatedate, expiredate)
        SELECT key, value, isbinary, insertdate, updatedate, expiredate
        FROM spring.state
        ON CONFLICT (key) DO UPDATE
        SET value = EXCLUDED.value,
            isbinary = EXCLUDED.isbinary,
            insertdate = LEAST(public.state.insertdate, EXCLUDED.insertdate),
            updatedate = GREATEST(
                COALESCE(public.state.updatedate, public.state.insertdate),
                COALESCE(EXCLUDED.updatedate, EXCLUDED.insertdate)),
            expiredate = EXCLUDED.expiredate
        WHERE COALESCE(EXCLUDED.updatedate, EXCLUDED.insertdate)
            >= COALESCE(public.state.updatedate, public.state.insertdate);
    END IF;

    IF to_regclass('spring.dapr_metadata') IS NOT NULL THEN
        IF to_regclass('public.dapr_metadata') IS NULL THEN
            EXECUTE 'CREATE TABLE public.dapr_metadata (LIKE spring.dapr_metadata INCLUDING ALL)';
        END IF;

        INSERT INTO public.dapr_metadata (key, value)
        SELECT key, value
        FROM spring.dapr_metadata
        ON CONFLICT (key) DO NOTHING;
    END IF;
END $$;
SQL
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
        -v "${DAPR_PRODUCTION_COMPONENTS_DIR}:/components:ro,Z" \
        -v "${DAPR_PRODUCTION_CONFIG_FILE}:/config/config.yaml:ro,Z" \
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
        -v "${DAPR_PRODUCTION_COMPONENTS_DIR}:/components:ro,Z" \
        -v "${DAPR_PRODUCTION_CONFIG_FILE}:/config/config.yaml:ro,Z" \
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
        die "SPRING_DISPATCHER_WORKER_TOKEN is not set. The dispatcher must be started first ('${HOST_SCRIPT##"${REPO_ROOT}"/} start') so it can write the bearer token to ${DISPATCHER_ENV_FILE} for the worker to source."
    fi
    # Worker MCP server port. Bound on `+` (all interfaces) inside the worker
    # container and published to the host so agent containers on the tenant
    # bridge can reach it via `host.docker.internal:${mcp_port}` (closes
    # #1199). The same value is set both as `-e Mcp__Port=` (so the worker's
    # IConfiguration picks it up and the listener binds the right port) and
    # `-p ${mcp_port}:${mcp_port}` (so the host port-maps to it). Override
    # with `Mcp__Port` in spring.env if 5050 conflicts on the host.
    local mcp_port="${Mcp__Port:-5050}"
    # Healthcheck rationale (#1600): the worker owns EF Core migrations via
    # DatabaseMigrator, an IHostedService whose StartAsync runs sequentially
    # before Kestrel's GenericWebHostService — so /health does not respond
    # until migrations have completed. cmd_up() waits on this signal before
    # starting spring-api; without it the API races the worker on a fresh
    # Postgres volume and logs `42703: column u.install_id does not exist`
    # for every directory query during the brief migration window.
    run_container spring-worker \
        --env-file "${RESOLVED_ENV_FILE}" \
        -p "${mcp_port}:${mcp_port}" \
        -e "DAPR_APP_ID=spring-worker" \
        -e "DAPR_HTTP_ENDPOINT=http://spring-worker-dapr:3500" \
        -e "DAPR_GRPC_ENDPOINT=http://spring-worker-dapr:50001" \
        -e "Dispatcher__BaseUrl=http://host.containers.internal:${dispatcher_port}/" \
        -e "Dispatcher__BearerToken=${SPRING_DISPATCHER_WORKER_TOKEN}" \
        -e "CallbackBaseUrl__BaseUrl=http://spring-caddy:8443/" \
        -e "Mcp__Port=${mcp_port}" \
        -v spring-dataprotection-keys:/home/app/.aspnet/DataProtection-Keys \
        --health-cmd 'curl -fsS http://localhost:8080/health || exit 1' \
        --health-interval 5s \
        --health-timeout 3s \
        --health-retries 30 \
        --health-start-period 5s \
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
#
# `restart --rebuild` (not `start`) is deliberate — see #1675 and #2220.
# `spring-voyage-host.sh start` short-circuits when a dispatcher PID is already
# live, which left stale dispatchers serving prior code (and, in the reported
# case, a deleted worktree cwd) for operators who ran `deploy.sh up` after a
# fresh pull. `restart` is a clean stop+start — a cold start path is an
# idempotent no-op on the stop side, so this is safe on a fresh machine too.
# `--rebuild` forces a publish before the new process launches so deploy.sh up
# is self-sufficient even when `build.sh` was not run immediately beforehand.
start_dispatcher() {
    [[ -x "${HOST_SCRIPT}" ]] || die "host-services script not found at ${HOST_SCRIPT} — run 'chmod +x ${HOST_SCRIPT}'"
    log "bouncing spring-dispatcher via ${HOST_SCRIPT##"${REPO_ROOT}"/} (restart --rebuild)"
    "${HOST_SCRIPT}" restart --rebuild
}

stop_dispatcher() {
    [[ -x "${HOST_SCRIPT}" ]] || return 0
    log "stopping spring-dispatcher via ${HOST_SCRIPT##"${REPO_ROOT}"/}"
    "${HOST_SCRIPT}" stop || true
}

start_api() {
    # DataProtection keys: see start_worker for the rationale (#337).
    #
    # Dispatcher wiring: the API host runs PersistentAgentRegistry as a
    # hosted service and calls the dispatcher on the restart path
    # (TryRestartAsync) and from the imperative deploy/undeploy
    # endpoints (AgentEndpoints, UnitEndpoints). Without these env vars
    # the registry's health timer crashes ~50s after boot and DELETEs
    # the persistent_agent_runtime row from its catch branch — see
    # issue #2518. cmd_up runs start_worker first, which already calls
    # load_dispatcher_env and exits if the token is missing, so the
    # API host transitively benefits from that guard; we re-source for
    # idempotency in case operators call `start_api` directly.
    load_dispatcher_env
    local dispatcher_port="${SPRING_DISPATCHER_PORT:-8090}"
    if [[ -z "${SPRING_DISPATCHER_WORKER_TOKEN:-}" ]]; then
        die "SPRING_DISPATCHER_WORKER_TOKEN is not set. The dispatcher must be started first ('${HOST_SCRIPT##"${REPO_ROOT}"/} start') so it can write the bearer token to ${DISPATCHER_ENV_FILE} for the API host to source."
    fi
    run_container spring-api \
        --env-file "${RESOLVED_ENV_FILE}" \
        -e "DAPR_APP_ID=spring-api" \
        -e "DAPR_HTTP_ENDPOINT=http://spring-api-dapr:3500" \
        -e "DAPR_GRPC_ENDPOINT=http://spring-api-dapr:50001" \
        -e "Dispatcher__BaseUrl=http://host.containers.internal:${dispatcher_port}/" \
        -e "Dispatcher__BearerToken=${SPRING_DISPATCHER_WORKER_TOKEN}" \
        -e "CallbackBaseUrl__BaseUrl=http://spring-caddy:8443/" \
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
        remove_container spring-ollama
        log "OLLAMA_MODE=host — skipping container. Ensure 'ollama serve' is running on the host."
        log "  macOS: brew install ollama && ollama serve"
        log "  Linux: https://ollama.com/download"
        log "Platform talks to it via LanguageModel__Ollama__BaseUrl (current ${LanguageModel__Ollama__BaseUrl:-http://host.containers.internal:11434})."
        log "Agent sidecars use the generated Dapr profile at ${Dapr__Sidecar__DelegatedSpringVoyageAgentComponentsPath:-${LOCAL_OLLAMA_COMPONENTS_DIR}}."
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
        -e OLLAMA_CONTEXT_LENGTH="${OLLAMA_CONTEXT_LENGTH:-16384}" \
        -e OLLAMA_KEEP_ALIVE="${OLLAMA_KEEP_ALIVE:-60m}" \
        ${gpu_args[@]+"${gpu_args[@]}"} \
        "${OLLAMA_IMAGE:-docker.io/ollama/ollama:latest}"

    # Dual-attach Ollama to the tenant network so agent containers (which
    # join spring-tenant-default — see ContainerConfigBuilder) can resolve
    # `spring-ollama:11434` from inside their own namespace without crossing
    # onto spring-net. ADR 0028 — Decision C (OSS slice) / issue #1160.
    ensure_tenant_network_attachment spring-ollama "${TENANT_NETWORK_NAME}"
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
    # Host-published ports. Caddy always listens on 80/443 *inside* the
    # container (the Caddyfile address derives from DEPLOY_SCHEME); only the
    # host-side mapping is configurable. Override CADDY_HTTP_PORT /
    # CADDY_HTTPS_PORT in spring.env when the host already runs something on
    # 80/443 — install.sh sets these automatically when its pre-flight detects
    # a conflict. Caveat: moving off 80/443 disables Caddy's automatic Let's
    # Encrypt, which validates against the public host's 80/443; front Caddy
    # with a reverse proxy or terminate TLS upstream in that case.
    local caddy_http_port="${CADDY_HTTP_PORT:-80}"
    local caddy_https_port="${CADDY_HTTPS_PORT:-443}"
    # Rootless privileged-port bindability is validated up front in preflight_up
    # (called by cmd_up) so the whole stack doesn't half-start before failing here.
    run_container spring-caddy \
        --env-file "${RESOLVED_ENV_FILE}" \
        -p "${caddy_http_port}:80" -p "${caddy_https_port}:443" \
        -v "${caddyfile}:/etc/caddy/Caddyfile:ro,Z" \
        -v spring-caddy-data:/data \
        -v spring-caddy-config:/config \
        "${CADDY_IMAGE:-docker.io/library/caddy:2}"

    # Dual-attach Caddy to the tenant network so agent containers and
    # workflow containers (which join spring-tenant-default — ADR 0028
    # Decision A) can reach the platform's authenticated Web API at
    # http://spring-caddy:8443 from inside the tenant namespace without
    # crossing onto spring-net. ADR 0028 Decision D, issue #1169.
    ensure_tenant_network_attachment spring-caddy "${TENANT_NETWORK_NAME}"
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
    # Polls the daprd HTTP healthz endpoint from a throwaway probe container
    # on spring-net. We can't `podman exec` the sidecar itself: daprio/dapr
    # is effectively distroless (no shell, no wget, no curl — just daprd),
    # so any in-container probe fails with "executable file not found" and
    # the readiness wait silently burns through its full timeout.
    #
    # The probe image is the platform image, already in the local store at
    # this point — pulled from the registry on a bundle install
    # (`SPRING_PLATFORM_IMAGE=ghcr.io/cvoya-com/spring-voyage:<v>`) or built
    # locally on a source deploy (`localhost/spring-voyage:latest`). #2198
    # dropped the external `docker.io/curlimages/curl` dependency so a fresh
    # install pulls no extra image just to issue a health probe.
    # SPRING_CURL_IMAGE remains as an operator override for mirrored /
    # air-gapped environments. Resolution order: explicit override →
    # the resolved platform image → the source-build default. The middle term
    # is essential in a bundle install, where localhost/spring-voyage:latest
    # does not exist and every probe would otherwise fail "image not found"
    # and burn the full timeout (#2595 follow-up).
    local name="$1" timeout="${2:-30}"
    local waited=0
    local curl_image="${SPRING_CURL_IMAGE:-${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}}"
    log "waiting for Dapr sidecar '${name}' to become ready"
    while (( waited < timeout )); do
        # /v1.0/healthz/outbound reports sidecar-only readiness (components
        # loaded, control-plane reachable). It does NOT require the paired
        # app to be up — which is what we want here, since the apps are
        # started immediately after.
        # --max-time 2 bounds each attempt so a real outage still fires the
        # overall deadline; -sf makes curl fail on non-2xx so the branch is
        # honest about real failures. --entrypoint= forces argv-only execution
        # so the platform image's default ENTRYPOINT (the .NET host) is not
        # invoked — the image only contributes its baked-in `curl` binary.
        if podman run --rm --network "${NETWORK_NAME}" \
                --entrypoint=curl "${curl_image}" \
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

# Best-effort podman-machine clock resync. The libkrun/QEMU VM clock freezes
# during host sleep (macOS/Windows) and falls behind real time; containers
# inherit the skew, so the GitHub connector signs App JWTs that GitHub rejects
# as "Bad credentials" (#2595). Running this on `up` means a deploy started
# after the host has slept begins with an honest clock. NEVER aborts the
# deploy — a missing script, no podman machine (native Linux), or a resync
# failure is logged and skipped.
resync_podman_machine_clock() {
    if [[ ! -x "${CLOCK_SCRIPT}" ]]; then
        log "clock-resync script not found at ${CLOCK_SCRIPT}; skipping clock check"
        return 0
    fi
    log "checking podman-machine clock skew (#2595)"
    if ! "${CLOCK_SCRIPT}"; then
        log "warning: podman-machine clock resync did not complete; if the GitHub"
        log "  connector later reports 'Bad credentials', run ${CLOCK_SCRIPT##"${REPO_ROOT}"/} manually"
    fi
    return 0
}

# ---------- commands ----------

# Fail fast on fresh-host problems before we start mutating container state:
#  - macOS without a running podman machine
#  - a missing locally-built platform image (build.sh / install.sh not run)
#  - host-published ports already occupied
#  - rootless Podman unable to bind privileged Caddy ports (80/443)
# This gives the from-source and standalone `deploy.sh up` paths the same
# actionable errors install.sh produces, instead of a cryptic podman failure
# part-way through the bring-up.
preflight_up() {
    # macOS needs a running podman machine before any podman call works.
    if [[ "$(uname -s)" == "Darwin" ]]; then
        if ! podman machine list --format '{{.Running}}' 2>/dev/null | grep -q '^true$'; then
            die "no running Podman machine. Run 'podman machine init && podman machine start' and retry."
        fi
    fi

    # A locally-built platform image is never pulled from a registry, so a
    # missing localhost/ ref means build.sh was not run. Registry refs (ghcr.io/…)
    # are left alone — podman pulls those on first use.
    local image="${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}"
    if [[ "${image}" == localhost/* ]] && ! podman image exists "${image}" 2>/dev/null; then
        die "platform image '${image}' is not in the local store — build it first (eng/build/build.sh), or for a release install let install.sh pull it."
    fi

    # Host-published container ports must be free. (The dispatcher's host port is
    # validated by spring-voyage-host.sh's own health probe.) A missing
    # lsof/ss/netstat yields 'unknown' (return 2) and we proceed rather than block.
    local caddy_http="${CADDY_HTTP_PORT:-80}" caddy_https="${CADDY_HTTPS_PORT:-443}" mcp="${Mcp__Port:-5050}"
    local entry label port
    for entry in "Caddy HTTP:${caddy_http}" "Caddy HTTPS:${caddy_https}" "worker MCP:${mcp}"; do
        label="${entry%%:*}"; port="${entry##*:}"
        if port_in_use "${port}"; then
            die "host port ${port} (${label}) is already in use. Free the service holding it, or set the matching override (CADDY_HTTP_PORT / CADDY_HTTPS_PORT / Mcp__Port) in ${ENV_FILE} and retry."
        fi
    done

    # Rootless Podman cannot bind privileged ports below the kernel floor, even
    # when free — fail with actionable guidance instead of the opaque
    # "rootlessport cannot expose privileged port" error at bind time. Linux +
    # rootless only (macOS forwards via the VM; rootful binds are unrestricted).
    if [[ "$(uname -s)" == "Linux" && "$(id -u)" -ne 0 ]]; then
        local floor; floor="$(unprivileged_port_floor)"
        if (( caddy_http < floor || caddy_https < floor )); then
            local target=$(( caddy_http < caddy_https ? caddy_http : caddy_https ))
            die "rootless Podman cannot bind Caddy host ports ${caddy_http}/${caddy_https} (kernel allows unprivileged binds only from port ${floor} up). Either:
  A) lower the threshold (keeps ${caddy_http}/${caddy_https} + automatic TLS):
       echo 'net.ipv4.ip_unprivileged_port_start=${target}' | sudo tee /etc/sysctl.d/99-spring-voyage.conf && sudo sysctl --system
  B) use high ports (no sudo): set CADDY_HTTP_PORT/CADDY_HTTPS_PORT (>= ${floor}) in spring.env and rerun."
        fi
    fi

    return 0
}

cmd_up() {
    parse_up_options "$@"
    require podman
    load_env
    preflight_up
    # Resync the container clock before starting the stack so the GitHub
    # connector never signs App JWTs with a post-sleep-skewed clock (#2595).
    resync_podman_machine_clock
    configure_local_ollama
    ensure_network "${NETWORK_NAME}"
    # Tenant network must exist before start_ollama tries to dual-attach.
    ensure_network "${TENANT_NETWORK_NAME}"

    start_postgres
    wait_healthy spring-postgres 60
    # Fail fast on a stale-volume password mismatch (network/scram auth) before
    # the worker hits it and crash-loops for 180s. See verify_postgres_password.
    verify_postgres_password
    repair_dapr_state_schema_drift
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
    ensure_delegated_dapr_tenant_attachments

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
    # container. We restart (not start-if-missing) so `deploy.sh up`
    # always picks up a freshly-published dispatcher binary and resets a
    # stale cwd — see #1675 and the comment above `start_dispatcher`.
    start_dispatcher

    start_worker
    # The worker's /health endpoint reports ready only after DatabaseMigrator's
    # IHostedService.StartAsync completes, so this gate keeps spring-api off
    # the database until every pending EF Core migration has been applied.
    # See #1600 — without this wait, a fresh Postgres volume races the API
    # against migrations and logs `42703: column u.install_id does not exist`.
    wait_healthy spring-worker 180
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

cmd_clean() {
    while [[ $# -gt 0 ]]; do
        case "$1" in
            -h|--help)
                usage
                return 0
                ;;
            *)
                die "unknown clean option: $1"
                ;;
        esac
    done

    require podman

    # Stop the host-process dispatcher first, matching cmd_down.
    stop_dispatcher

    while IFS= read -r container; do
        [[ -n "${container}" ]] || continue
        remove_container "${container}"
    done < <(owned_runtime_containers)

    for svc in "${SERVICES[@]}"; do
        remove_container "${svc}"
    done

    for volume in "${VOLUMES[@]}"; do
        remove_volume "${volume}"
    done

    while IFS= read -r net; do
        [[ -n "${net}" ]] || continue
        remove_network "${net}"
    done < <(owned_runtime_networks)
    while IFS= read -r net; do
        [[ -n "${net}" ]] || continue
        remove_network "${net}"
    done < <(owned_user_networks)
    remove_network "${TENANT_NETWORK_NAME}"
    remove_network "${NETWORK_NAME}"
    remove_generated_local_ollama_profile

    if [[ -x "${BUILD_SCRIPT}" ]]; then
        log "removing Spring Voyage image refs via ${BUILD_SCRIPT##"${REPO_ROOT}"/} clean"
        if ! "${BUILD_SCRIPT}" clean; then
            log "warning: image cleanup failed; deployment containers, volumes, and networks were still cleaned"
        fi
    elif [[ -f "${SCRIPT_DIR}/manifest.json" ]]; then
        # Bundle mode: remove the pinned platform image directly from manifest.json.
        local platform_image
        platform_image="$(awk -F'"' '/"platform_image"/{print $4}' "${SCRIPT_DIR}/manifest.json" 2>/dev/null || true)"
        if [[ -n "${platform_image}" ]]; then
            log "removing platform image ${platform_image}"
            podman rmi "${platform_image}" 2>/dev/null || log "warning: failed to remove image ${platform_image}"
        else
            log "warning: could not read platform_image from manifest.json; skipping image cleanup"
        fi
    else
        log "warning: build script not found at ${BUILD_SCRIPT}; skipping image cleanup"
    fi

    log "clean complete"
}

cmd_restart() {
    for arg in "$@"; do
        case "${arg}" in
            -h|--help)
                usage
                exit 0
                ;;
        esac
    done
    cmd_down
    cmd_up "$@"
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

cmd_build_deprecated() {
    [[ -x "${BUILD_SCRIPT}" ]] || die "build script not found at ${BUILD_SCRIPT}"
    log "'deploy.sh build' is deprecated; use './build.sh' instead."
    "${BUILD_SCRIPT}" "$@"
}

cmd_ensure_user_net() {
    require podman
    ensure_user_network "${1:-}"
}

usage() {
    cat <<EOF
Spring Voyage — Podman deployment

Commands:
  init                   First-run bootstrap: copy spring.env.example to
                         spring.env (if missing) and provision a fresh
                         SPRING_SECRETS_AES_KEY. Refuses to overwrite an
                         existing key.
  up                     Start the full stack on ${NETWORK_NAME}
  down                   Stop and remove containers (keeps volumes)
  clean                  Stop deploy-owned containers, remove deploy-owned
                         volumes and networks, remove platform runtime
                         containers, and remove Spring Voyage image refs
                         via build.sh clean
  restart                down + up
                         Supports the same options as 'up'.
  status                 Show container status
  logs [service]         Follow logs (all services if omitted)
  ensure-user-net <uid>  Create per-user bridge network for agent isolation

Options for 'up' and 'restart':
  --local-ollama                 Use a host-installed Ollama service instead
                                 of the spring-ollama container. The script
                                 verifies /api/tags before starting the stack.
  --ollama-endpoint <host-or-url> Container-facing Ollama endpoint. Defaults to
                                 http://host.containers.internal.
  --ollama-port <port>           Ollama port. Defaults to 11434.

Environment file: ${ENV_FILE}
  Override with SPRING_ENV_FILE=/path/to/other.env

The first time you deploy, run 'init' to generate the secrets key, then
'./build.sh', then './deploy.sh up'.
The key in spring.env is the only thing that can decrypt secrets in the
state store — back it up alongside the postgres volume.
EOF
}

main() {
    local subcommand="${1:-}"
    shift || true
    case "${subcommand}" in
        init)                cmd_init "$@" ;;
        up)                  cmd_up "$@" ;;
        down)                cmd_down "$@" ;;
        clean)               cmd_clean "$@" ;;
        restart)             cmd_restart "$@" ;;
        status)              cmd_status "$@" ;;
        logs)                cmd_logs "$@" ;;
        build)               cmd_build_deprecated "$@" ;;
        ensure-user-net)     cmd_ensure_user_net "$@" ;;
        ""|-h|--help|help)   usage ;;
        *)                   usage; exit 2 ;;
    esac
}

main "$@"
