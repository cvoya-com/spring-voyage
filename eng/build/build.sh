#!/usr/bin/env bash
# Spring Voyage — local Podman image build.
#
# Builds the platform image, the bundled agent images, and the
# spring-dispatcher host binary. Deployment lifecycle commands live in
# deploy.sh; this script owns only build-time work.
#
# Usage:
#   ./build.sh                # build platform + bundled agent images
#   ./build.sh build          # same as above
#   ./build.sh clean          # remove local image refs and dispatcher publish output
#   ./build.sh --ghcr-only    # skip localhost/... compatibility aliases
#
# Environment: optionally reads ./spring.env (or $SPRING_ENV_FILE) for image
# tags such as SPRING_PLATFORM_IMAGE and SPRING_AGENT_TAG. The file is not
# required for builds; defaults are used when it is absent.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
ENV_FILE="${SPRING_ENV_FILE:-${SCRIPT_DIR}/spring.env}"
HOST_SCRIPT="${SCRIPT_DIR}/spring-voyage-host.sh"

log() { printf '[build] %s\n' "$*" >&2; }
die() { printf '[build][error] %s\n' "$*" >&2; exit 1; }

require() {
    command -v "$1" >/dev/null 2>&1 || die "required command '$1' not found on PATH"
}

load_image_env() {
    if [[ ! -f "${ENV_FILE}" ]]; then
        log "env file not found: ${ENV_FILE}; using default image tags"
        return 0
    fi

    # Source the env file only for build-time knobs such as
    # SPRING_PLATFORM_IMAGE and SPRING_AGENT_TAG. Runtime secret validation
    # belongs to deploy.sh up, not to image builds.
    set -a
    # shellcheck disable=SC1090
    source "${ENV_FILE}"
    set +a
}

usage() {
    cat <<EOF
Spring Voyage — Podman image build

Usage:
  ./build.sh [build] [--ghcr-only]
  ./build.sh build [--ghcr-only]
  ./build.sh clean

Commands:
  build        Build platform image, bundled agent images, and dispatcher.
               This is the default when no command is supplied.
  clean        Remove Spring Voyage image refs created by build.sh and delete
               the dispatcher publish output.

Options:
  --ghcr-only  Tag only canonical ghcr.io/... agent image refs. By default,
               the agent-image build also writes localhost/... aliases for
               older local dev workflows.

Environment file: ${ENV_FILE}
  Override with SPRING_ENV_FILE=/path/to/other.env
EOF
}

local_image_refs() {
    local agent_tag="${SPRING_AGENT_TAG:-latest}"

    # Remove dependent / alias tags before the shared base tag so Podman can
    # delete refs without forcing images that still have child images.
    printf '%s\n' \
        "${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}" \
        "localhost/spring-voyage-agent-claude-code:${agent_tag}" \
        "localhost/spring-voyage-agent-gemini:${agent_tag}" \
        "localhost/spring-voyage-agent:${agent_tag}" \
        "ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering:${agent_tag}" \
        "ghcr.io/cvoya-com/spring-voyage-agent-oss-design:${agent_tag}" \
        "ghcr.io/cvoya-com/spring-voyage-agent-oss-product-management:${agent_tag}" \
        "ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management:${agent_tag}" \
        "ghcr.io/cvoya-com/spring-voyage-claude-code-base:${agent_tag}" \
        "ghcr.io/cvoya-com/spring-voyage-gemini-base:${agent_tag}" \
        "ghcr.io/cvoya-com/spring-voyage-agent:${agent_tag}" \
        "ghcr.io/cvoya-com/spring-voyage-agent-base:${agent_tag}"
}

remove_image_ref() {
    local ref="$1"
    [[ -n "${ref}" ]] || return 0
    if podman image exists "${ref}" 2>/dev/null; then
        log "removing image ref ${ref}"
        if ! podman rmi "${ref}" >/dev/null 2>&1; then
            log "warning: could not remove ${ref}; it may still be used by a container or child image"
        fi
    fi
}

clean_dispatcher_publish() {
    local publish_dir="${SPRING_DISPATCHER_PUBLISH_DIR:-${REPO_ROOT}/.spring-voyage/dispatcher/publish}"
    if [[ -d "${publish_dir}" ]]; then
        log "removing dispatcher publish output: ${publish_dir}"
        rm -rf -- "${publish_dir}"
    fi
}

cmd_build() {
    local ghcr_only=0
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --ghcr-only)
                ghcr_only=1
                shift
                ;;
            -h|--help)
                usage
                return 0
                ;;
            *)
                die "unknown build option: $1"
                ;;
        esac
    done

    require podman
    load_image_env
    log "building platform image: ${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}"
    podman build \
        -f "${SCRIPT_DIR}/Dockerfile" \
        -t "${SPRING_PLATFORM_IMAGE:-localhost/spring-voyage:latest}" \
        "${REPO_ROOT}"

    # The tool-bearing agent images are built under their canonical GHCR
    # refs so the dispatcher can satisfy runtime-catalog defaults from the
    # local image store before attempting a registry pull. Localhost aliases
    # are only a dev convenience and can be suppressed for release-style
    # builds.
    log "building agent images via eng/build/build-agent-images.sh"
    local agent_build_args=(--tag "${SPRING_AGENT_TAG:-latest}")
    if [[ "${ghcr_only}" -eq 1 ]]; then
        agent_build_args+=(--ghcr-only)
    fi
    DOCKER=podman "${SCRIPT_DIR}/build-agent-images.sh" "${agent_build_args[@]}"

    # spring-dispatcher is a host process (#1063); publish its .NET binary
    # instead of producing an image.
    if [[ -x "${HOST_SCRIPT}" ]]; then
        log "publishing spring-dispatcher host binary"
        "${HOST_SCRIPT}" build
    fi
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
    load_image_env
    while IFS= read -r ref; do
        remove_image_ref "${ref}"
    done < <(local_image_refs)
    clean_dispatcher_publish
    log "clean complete"
}

main() {
    local subcommand="${1:-}"
    case "${subcommand}" in
        build)
            shift
            cmd_build "$@"
            ;;
        clean)
            shift
            cmd_clean "$@"
            ;;
        "")
            cmd_build
            ;;
        --ghcr-only)
            cmd_build "$@"
            ;;
        -h|--help|help)
            usage
            ;;
        *)
            cmd_build "$@"
            ;;
    esac
}

main "$@"
