#!/usr/bin/env bash
# Spring Voyage — level-based reset of a local Podman deployment.
#
# Different from `deploy.sh clean` in that it offers four reset levels with
# explicit, progressive scope. Each level keeps more (lighter) or fewer
# (heavier) caches so an operator doesn't have to re-download or re-build
# things they don't actually need to refresh.
#
# Levels (lightest -> heaviest):
#
#   state      Remove platform containers and platform volumes (postgres,
#              redis, dapr placement/scheduler state, dataprotection-keys,
#              caddy data/config). KEEPS spring-ollama-data (LLM models),
#              all images, all networks, dispatcher publish output.
#              Use: "Wipe app data; keep everything else."
#
#   platform   `state` + remove agent workspace volumes (spring-ws-*),
#              built spring-voyage* images, dispatcher publish output,
#              runtime ephemeral/persistent containers, and all platform
#              networks. KEEPS spring-ollama-data and base images
#              (postgres, redis, dapr, caddy, ollama/ollama).
#              Use: "Rebuild Spring Voyage; keep cached base images and
#                    LLM models."
#
#   deep       `platform` + remove spring-ollama-data (LLM models).
#              KEEPS base images.
#              Use: "Full data wipe; keep cached base images."
#
#   total      Nuclear: `deep` + remove base images.
#              Use: "Reproduce a brand-new install from zero."
#
# Out of scope: the dispatcher's host state at ~/.spring-voyage/host/ and
# the install root at ~/.spring-voyage/. Removing those is uninstall's
# job — see `eng/install/uninstall.sh --purge`.
#
# Usage:
#   ./reset.sh <level> [--yes] [--dry-run]
#   ./reset.sh state              # interactive confirmation, then run
#   ./reset.sh platform --yes     # no prompt
#   ./reset.sh total --dry-run    # print actions only

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
CONFIG_DIR="${REPO_ROOT}/eng/config"
ENV_FILE="${SPRING_ENV_FILE:-${CONFIG_DIR}/spring.env}"
HOST_SCRIPT="${SCRIPT_DIR}/spring-voyage-host.sh"
BUILD_SCRIPT="${REPO_ROOT}/eng/build/build.sh"

# Constants duplicated from deploy.sh. Keep them in sync — deploy.sh is
# the authority on the platform topology.
NETWORK_NAME="spring-net"
TENANT_NETWORK_NAME="spring-tenant-default"
USER_NETWORK_PREFIX="spring-user-"
RUNTIME_NETWORK_PATTERN='^spring-net-[[:xdigit:]]+'
RUNTIME_CONTAINER_NAME_PATTERN='^spring-(persistent|ephemeral|exec|dapr)-'
RUNTIME_VOLUME_PATTERN='^spring-ws-'

PLATFORM_CONTAINERS=(
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

# Volumes wiped on `state` and above. Excludes spring-ollama-data so
# operators don't have to re-download LLM models on every reset.
PLATFORM_VOLUMES=(
    spring-postgres-data
    spring-redis-data
    spring-placement-data
    spring-scheduler-data
    spring-dataprotection-keys
    spring-caddy-data
    spring-caddy-config
)

# The model-cache volume — removed on `deep` and above.
MODELS_VOLUME="spring-ollama-data"

# Base images — removed only on `total`. Defaults match deploy.sh; resolved
# from spring.env in load_env so user overrides take effect.
BASE_IMAGES=(
    "docker.io/library/postgres:17"
    "docker.io/library/redis:7"
    "docker.io/library/caddy:2"
    "docker.io/daprio/dapr:1.17.4"
    "docker.io/ollama/ollama:latest"
)

LEVEL=""
ASSUME_YES=0
DRY_RUN=0

# ---------------------------------------------------------------------------
# Output helpers
# ---------------------------------------------------------------------------
if [[ -t 1 ]]; then
    BOLD=$'\033[1m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[1;33m'; RED=$'\033[0;31m'; CYAN=$'\033[0;36m'; NC=$'\033[0m'
else
    BOLD=''; GREEN=''; YELLOW=''; RED=''; CYAN=''; NC=''
fi
header() { printf "\n%s%s==> %s%s\n" "${BOLD}" "${CYAN}" "$1" "${NC}"; }
info()   { printf "    %s\n" "$*"; }
ok()     { printf "  %s✓%s  %s\n" "${GREEN}" "${NC}" "$*"; }
warn()   { printf "  %s!%s  %s\n" "${YELLOW}" "${NC}" "$*" >&2; }
err()    { printf "  %s✗%s  %s\n" "${RED}" "${NC}" "$*" >&2; }
die()    { err "$*"; exit 1; }
plan()   { printf "    %splan%s %s\n" "${CYAN}" "${NC}" "$*"; }

usage() {
    cat <<EOF
Spring Voyage — level-based reset of a local Podman deployment.

Usage:
  reset.sh <level> [--yes|-y] [--dry-run|-n]
  reset.sh -h | --help

Levels (lightest -> heaviest):
  state       Remove platform containers; remove platform volumes
              (postgres, redis, dapr state, dataprotection-keys,
              caddy data/config).
              KEEPS spring-ollama-data, all images, all networks.
              Use: "Wipe app data; keep everything else."

  platform    state + remove agent workspace volumes (spring-ws-*),
              built spring-voyage* images, dispatcher publish output,
              runtime ephemeral/persistent containers, and all platform
              networks.
              KEEPS spring-ollama-data and base images
              (postgres, redis, dapr, caddy, ollama/ollama).
              Use: "Rebuild Spring Voyage; keep cached base images
                    and LLM models."

  deep        platform + remove spring-ollama-data (LLM models).
              KEEPS base images.
              Use: "Full data wipe; keep cached base images."

  total       deep + remove base images.
              Use: "Reproduce a brand-new install from zero."

Options:
  -y, --yes      Skip the interactive confirmation prompt.
  -n, --dry-run  Print what would be removed; do not change anything.
  -h, --help     Show this message.

Out of scope: ~/.spring-voyage/host/ (dispatcher state) and the install
root. Use \`eng/install/uninstall.sh --purge\` to remove those.

Environment file: ${ENV_FILE}
  Override with SPRING_ENV_FILE=/path/to/other.env so base-image refs
  match what your deployment actually pulled.
EOF
}

require() {
    command -v "$1" >/dev/null 2>&1 || die "required command '$1' not found on PATH"
}

# Sources spring.env so user-overridden image refs (POSTGRES_IMAGE, ...)
# match deploy.sh's resolution.
load_env() {
    if [[ -f "${ENV_FILE}" ]]; then
        set -a
        # shellcheck disable=SC1090
        source "${ENV_FILE}"
        set +a
    fi
    BASE_IMAGES=(
        "${POSTGRES_IMAGE:-docker.io/library/postgres:17}"
        "${REDIS_IMAGE:-docker.io/library/redis:7}"
        "${CADDY_IMAGE:-docker.io/library/caddy:2}"
        "${DAPR_IMAGE:-docker.io/daprio/dapr:1.17.4}"
        "${OLLAMA_IMAGE:-docker.io/ollama/ollama:latest}"
    )
}

# ---------------------------------------------------------------------------
# Inventory helpers — return rows of currently-present resources.
# ---------------------------------------------------------------------------
owned_runtime_containers() {
    podman ps -a --format '{{.Names}}' 2>/dev/null | grep -E "${RUNTIME_CONTAINER_NAME_PATTERN}" || true
}

owned_runtime_networks() {
    podman network ls --format '{{.Name}}' 2>/dev/null | grep -E "${RUNTIME_NETWORK_PATTERN}" || true
}

owned_user_networks() {
    podman network ls --format '{{.Name}}' 2>/dev/null | grep -E "^${USER_NETWORK_PREFIX}" || true
}

owned_runtime_volumes() {
    podman volume ls --format '{{.Name}}' 2>/dev/null | grep -E "${RUNTIME_VOLUME_PATTERN}" || true
}

container_exists() { podman container exists "$1" 2>/dev/null; }
volume_exists()    { podman volume exists "$1" 2>/dev/null; }
network_exists()   { podman network exists "$1" 2>/dev/null; }
image_exists()     { podman image exists "$1" 2>/dev/null; }

# ---------------------------------------------------------------------------
# Action wrappers — honor --dry-run by printing the planned action.
# ---------------------------------------------------------------------------
do_remove_container() {
    local name="$1"
    container_exists "${name}" || return 0
    if [[ "${DRY_RUN}" -eq 1 ]]; then
        plan "podman rm -f ${name}"
        return 0
    fi
    if podman rm -f "${name}" >/dev/null 2>&1; then
        ok "removed container ${name}"
    else
        warn "failed to remove container ${name}"
    fi
}

do_remove_volume() {
    local name="$1"
    volume_exists "${name}" || return 0
    if [[ "${DRY_RUN}" -eq 1 ]]; then
        plan "podman volume rm -f ${name}"
        return 0
    fi
    if podman volume rm -f "${name}" >/dev/null 2>&1; then
        ok "removed volume ${name}"
    else
        warn "failed to remove volume ${name}"
    fi
}

do_remove_network() {
    local name="$1"
    network_exists "${name}" || return 0
    if [[ "${DRY_RUN}" -eq 1 ]]; then
        plan "podman network rm ${name}"
        return 0
    fi
    if podman network rm "${name}" >/dev/null 2>&1; then
        ok "removed network ${name}"
    else
        warn "failed to remove network ${name} (may still have non-deploy containers attached)"
    fi
}

do_remove_image() {
    local ref="$1"
    [[ -n "${ref}" ]] || return 0
    image_exists "${ref}" || return 0
    if [[ "${DRY_RUN}" -eq 1 ]]; then
        plan "podman rmi ${ref}"
        return 0
    fi
    if podman rmi "${ref}" >/dev/null 2>&1; then
        ok "removed image ${ref}"
    else
        warn "failed to remove image ${ref} (may still be used by a container or child image)"
    fi
}

# Stops the host-process dispatcher. Used by every level so podman calls
# from in-flight ContainerLifecycleManager invocations don't race the
# reset.
stop_dispatcher() {
    [[ -x "${HOST_SCRIPT}" ]] || return 0
    if [[ "${DRY_RUN}" -eq 1 ]]; then
        plan "${HOST_SCRIPT##"${REPO_ROOT}"/} stop"
        return 0
    fi
    if "${HOST_SCRIPT}" stop >/dev/null 2>&1; then
        ok "stopped spring-dispatcher host process"
    fi
}

# Calls build.sh clean to remove spring-voyage built images + dispatcher
# publish output in one shot.
clean_built_artifacts() {
    if [[ ! -x "${BUILD_SCRIPT}" ]]; then
        warn "build script not found at ${BUILD_SCRIPT}; skipping platform image cleanup"
        return 0
    fi
    if [[ "${DRY_RUN}" -eq 1 ]]; then
        plan "${BUILD_SCRIPT##"${REPO_ROOT}"/} clean"
        return 0
    fi
    if "${BUILD_SCRIPT}" clean; then
        ok "removed Spring Voyage built images + dispatcher publish output"
    else
        warn "build.sh clean returned non-zero; continuing"
    fi
}

# ---------------------------------------------------------------------------
# Level steps. Each step is composable so heavier levels reuse lighter steps.
# ---------------------------------------------------------------------------
step_remove_platform_containers() {
    header "Removing platform containers"
    for c in "${PLATFORM_CONTAINERS[@]}"; do
        do_remove_container "${c}"
    done
}

step_remove_platform_volumes() {
    header "Removing platform volumes (keeping ${MODELS_VOLUME})"
    for v in "${PLATFORM_VOLUMES[@]}"; do
        do_remove_volume "${v}"
    done
}

step_remove_runtime_containers() {
    header "Removing runtime containers (ephemeral/persistent agents)"
    local any=0
    while IFS= read -r c; do
        [[ -n "${c}" ]] || continue
        any=1
        do_remove_container "${c}"
    done < <(owned_runtime_containers)
    [[ "${any}" -eq 0 ]] && info "(none found)"
    return 0
}

step_remove_runtime_volumes() {
    header "Removing agent workspace volumes (spring-ws-*)"
    local any=0
    while IFS= read -r v; do
        [[ -n "${v}" ]] || continue
        any=1
        do_remove_volume "${v}"
    done < <(owned_runtime_volumes)
    [[ "${any}" -eq 0 ]] && info "(none found)"
    return 0
}

step_remove_built_images() {
    header "Removing Spring Voyage built images + dispatcher publish output"
    clean_built_artifacts
}

step_remove_networks() {
    header "Removing platform networks"
    local any=0
    while IFS= read -r n; do
        [[ -n "${n}" ]] || continue
        any=1
        do_remove_network "${n}"
    done < <(owned_runtime_networks)
    while IFS= read -r n; do
        [[ -n "${n}" ]] || continue
        any=1
        do_remove_network "${n}"
    done < <(owned_user_networks)
    network_exists "${TENANT_NETWORK_NAME}" && any=1
    do_remove_network "${TENANT_NETWORK_NAME}"
    network_exists "${NETWORK_NAME}" && any=1
    do_remove_network "${NETWORK_NAME}"
    [[ "${any}" -eq 0 ]] && info "(none found)"
    return 0
}

step_remove_models_volume() {
    header "Removing LLM model volume (${MODELS_VOLUME})"
    do_remove_volume "${MODELS_VOLUME}"
}

step_remove_base_images() {
    header "Removing base images"
    for img in "${BASE_IMAGES[@]}"; do
        do_remove_image "${img}"
    done
}

# ---------------------------------------------------------------------------
# Confirmation
# ---------------------------------------------------------------------------
confirm() {
    [[ "${ASSUME_YES}" -eq 1 ]] && return 0
    [[ "${DRY_RUN}" -eq 1 ]] && return 0
    printf "\n  %sProceed with %s reset?%s [y/N]: " "${BOLD}" "${LEVEL}" "${NC}" >&2
    local answer
    if [[ -t 0 ]]; then
        IFS= read -r answer || answer=""
    elif [[ -r /dev/tty ]]; then
        IFS= read -r answer </dev/tty || answer=""
    else
        answer=""
    fi
    case "${answer:-N}" in
        [Yy]|[Yy][Ee][Ss]) return 0 ;;
        *) die "Aborted by user." ;;
    esac
}

show_plan() {
    header "Reset plan: ${BOLD}${LEVEL}${NC}"
    case "${LEVEL}" in
        state)
            info "Will remove:"
            info "  - platform containers (${#PLATFORM_CONTAINERS[@]}): ${PLATFORM_CONTAINERS[*]}"
            info "  - platform volumes (${#PLATFORM_VOLUMES[@]}): ${PLATFORM_VOLUMES[*]}"
            info "Will preserve: ${MODELS_VOLUME}, all images, all networks,"
            info "                dispatcher publish output."
            ;;
        platform)
            info "Will remove:"
            info "  - platform containers (${#PLATFORM_CONTAINERS[@]})"
            info "  - runtime ephemeral/persistent agent containers"
            info "  - platform volumes (${#PLATFORM_VOLUMES[@]})"
            info "  - agent workspace volumes (spring-ws-*)"
            info "  - Spring Voyage built images (spring-voyage, spring-voyage-agent-*)"
            info "  - dispatcher publish output"
            info "  - platform networks (${NETWORK_NAME}, ${TENANT_NETWORK_NAME}, spring-net-*, ${USER_NETWORK_PREFIX}*)"
            info "Will preserve: ${MODELS_VOLUME}, base images (postgres, redis, dapr, caddy, ollama/ollama)."
            ;;
        deep)
            info "Will remove:"
            info "  - everything from 'platform' +"
            info "  - LLM model volume (${MODELS_VOLUME})"
            info "Will preserve: base images only."
            ;;
        total)
            info "Will remove:"
            info "  - everything from 'deep' +"
            info "  - base images: ${BASE_IMAGES[*]}"
            info "Will preserve: nothing podman-side. Re-deploy will re-pull every image."
            ;;
    esac
    if [[ "${DRY_RUN}" -eq 1 ]]; then
        info ""
        info "${YELLOW}--dry-run mode: no changes will be made.${NC}"
    fi
}

# ---------------------------------------------------------------------------
# Level dispatchers — composed from steps so heavier levels reuse lighter ones.
# ---------------------------------------------------------------------------
do_state() {
    stop_dispatcher
    step_remove_platform_containers
    step_remove_platform_volumes
}

do_platform() {
    stop_dispatcher
    step_remove_platform_containers
    step_remove_runtime_containers
    step_remove_platform_volumes
    step_remove_runtime_volumes
    step_remove_built_images
    step_remove_networks
}

do_deep() {
    do_platform
    step_remove_models_volume
}

do_total() {
    do_deep
    step_remove_base_images
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
parse_args() {
    while [[ $# -gt 0 ]]; do
        case "$1" in
            -h|--help) usage; exit 0 ;;
            -y|--yes) ASSUME_YES=1; shift ;;
            -n|--dry-run) DRY_RUN=1; shift ;;
            state|platform|deep|total)
                if [[ -n "${LEVEL}" ]]; then
                    die "level already set to '${LEVEL}'; cannot also set '$1'"
                fi
                LEVEL="$1"
                shift
                ;;
            -*) die "unknown option: $1 (try --help)" ;;
            *)  die "unknown argument: $1 (try --help)" ;;
        esac
    done
    if [[ -z "${LEVEL}" ]]; then
        usage >&2
        printf '\n' >&2
        die "missing level. Pick one of: state, platform, deep, total."
    fi
}

main() {
    parse_args "$@"
    require podman
    load_env

    show_plan
    confirm

    case "${LEVEL}" in
        state)    do_state ;;
        platform) do_platform ;;
        deep)     do_deep ;;
        total)    do_total ;;
    esac

    header "${LEVEL} reset complete"
    if [[ "${DRY_RUN}" -eq 1 ]]; then
        info "Dry-run only — no changes were made."
    else
        info "Run \`./deploy.sh up\` to bring the stack back up."
    fi
}

main "$@"
