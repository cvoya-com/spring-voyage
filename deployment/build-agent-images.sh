#!/usr/bin/env bash
# deployment/build-agent-images.sh — single entry point for building every
# agent image the Spring Voyage dispatcher launches today (PR 3b of #1087,
# #1096; omnibus added in #1514).
#
# Builds four images, in dependency order:
#   1. ghcr.io/cvoya-com/spring-voyage-agent-base:<tag>  (path-1 BYOI base)
#   2. localhost/spring-voyage-agent-claude-code:<tag>   (path-1 reference, FROMs #1)
#   3. localhost/spring-voyage-agent-dapr:<tag>          (path-3 native A2A)
#   4. ghcr.io/cvoya-com/spring-voyage-agents:<tag>      (omnibus default, FROMs #1)
#
# Conformance paths are documented in
# `docs/architecture/agent-runtime.md` § 7. The ghcr-namespaced images are
# the same artifacts the `release-agent-base.yml` and
# `release-spring-voyage-agents.yml` workflows publish on tag push — building
# locally here is the offline fallback for laptops + CI runs without GHCR
# pull access.
#
# Usage:
#   deployment/build-agent-images.sh                # builds :dev tags
#   deployment/build-agent-images.sh --tag 1.2.3    # builds :1.2.3 tags
#   deployment/build-agent-images.sh --help
#
# Environment overrides (see --help for the full list):
#   DOCKER         — `docker` (default) or `podman`. Auto-detects if unset.
#   AGENT_BASE_IMAGE — pin a published agent-base tag for the claude-code
#                      and omnibus builds instead of the locally-built one.
#                      Lets CI verify the published image without rebuilding.
#
# Mirrors the structure and style of `deployment/build-sidecar.sh` so an
# operator who knows one knows the other.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

TAG="dev"
SKIP_AGENT_BASE=0
AGENT_BASE_OVERRIDE="${AGENT_BASE_IMAGE:-}"

usage() {
    cat <<EOF
Usage: deployment/build-agent-images.sh [options]

Builds, in order:
  1. ghcr.io/cvoya-com/spring-voyage-agent-base:<tag>
  2. localhost/spring-voyage-agent-claude-code:<tag>
  3. localhost/spring-voyage-agent-dapr:<tag>
  4. ghcr.io/cvoya-com/spring-voyage-agents:<tag>    (omnibus)

Options:
  --tag <value>            Tag suffix for all images (default: dev).
  --skip-agent-base        Skip building spring-voyage-agent-base:<tag>.
                           Useful when --agent-base-image points at an
                           already-pulled / already-built reference.
  --agent-base-image <ref> Override the FROM line of the claude-code and
                           omnibus images. Defaults to
                           ghcr.io/cvoya-com/spring-voyage-agent-base:<tag>
                           (the tag built in step 1). Honors the
                           AGENT_BASE_IMAGE env var.
  -h, --help               Show this help.

Environment:
  DOCKER                   Container CLI to use. Defaults to 'docker' if
                           on PATH, else 'podman'. Set explicitly to
                           force one runtime over the other.

Examples:
  # Local dev, all four images at :dev:
  deployment/build-agent-images.sh

  # Verify the published agent-base image works:
  deployment/build-agent-images.sh --skip-agent-base \\
                                   --agent-base-image ghcr.io/cvoya-com/spring-voyage-agent-base:1.0.0

  # Cut release artifacts to the registry-shaped tag:
  deployment/build-agent-images.sh --tag 1.2.3
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --tag)
            TAG="${2:?--tag requires a value}"
            shift 2
            ;;
        --tag=*)
            TAG="${1#*=}"
            shift
            ;;
        --skip-agent-base)
            SKIP_AGENT_BASE=1
            shift
            ;;
        --agent-base-image)
            AGENT_BASE_OVERRIDE="${2:?--agent-base-image requires a value}"
            shift 2
            ;;
        --agent-base-image=*)
            AGENT_BASE_OVERRIDE="${1#*=}"
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "::error::unknown argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

# Resolve the container CLI. We prefer docker (matches build-sidecar.sh
# and the release workflow) but fall back to podman so the script works
# on macOS Apple-silicon laptops that ship podman (cf. deploy.sh, which
# is podman-only).
if [[ -z "${DOCKER:-}" ]]; then
    if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
        DOCKER=docker
    elif command -v podman >/dev/null 2>&1; then
        DOCKER=podman
    else
        echo "::error::neither docker nor podman is on PATH (or docker daemon is unreachable)" >&2
        exit 1
    fi
fi

if ! command -v "${DOCKER}" >/dev/null 2>&1; then
    echo "::error::container CLI '${DOCKER}' not found on PATH" >&2
    exit 1
fi

log() { printf '[build-agent-images] %s\n' "$*" >&2; }

AGENT_BASE_IMAGE="ghcr.io/cvoya-com/spring-voyage-agent-base"
CLAUDE_IMAGE="localhost/spring-voyage-agent-claude-code"
DAPR_IMAGE="localhost/spring-voyage-agent-dapr"
AGENTS_IMAGE="ghcr.io/cvoya-com/spring-voyage-agents"

# ---- 1. agent-base -------------------------------------------------------
if [[ "${SKIP_AGENT_BASE}" -eq 1 ]]; then
    log "skipping agent-base build (--skip-agent-base)"
else
    log "building ${AGENT_BASE_IMAGE}:${TAG}"
    "${DOCKER}" build \
        --file "${SCRIPT_DIR}/Dockerfile.agent-base" \
        --tag "${AGENT_BASE_IMAGE}:${TAG}" \
        "${REPO_ROOT}"
fi

# Default the claude-code and omnibus FROM to whatever we just built (or to
# the user's pinned override). This is what makes the script work both online
# (CI verifying the published image) and offline (laptop without GHCR access).
if [[ -z "${AGENT_BASE_OVERRIDE}" ]]; then
    AGENT_BASE_OVERRIDE="${AGENT_BASE_IMAGE}:${TAG}"
fi

# ---- 2. agent-claude-code (path 1) ---------------------------------------
log "building ${CLAUDE_IMAGE}:${TAG} (FROM ${AGENT_BASE_OVERRIDE})"
"${DOCKER}" build \
    --file "${SCRIPT_DIR}/Dockerfile.agent.claude-code" \
    --build-arg "AGENT_BASE_IMAGE=${AGENT_BASE_OVERRIDE}" \
    --tag "${CLAUDE_IMAGE}:${TAG}" \
    "${REPO_ROOT}"

# ---- 3. agent-dapr (path 3 — native A2A) ---------------------------------
log "building ${DAPR_IMAGE}:${TAG}"
"${DOCKER}" build \
    --file "${SCRIPT_DIR}/Dockerfile.agent.dapr" \
    --tag "${DAPR_IMAGE}:${TAG}" \
    "${REPO_ROOT}"

# ---- 4. spring-voyage-agents (omnibus default) ---------------------------
log "building ${AGENTS_IMAGE}:${TAG} (FROM ${AGENT_BASE_OVERRIDE})"
"${DOCKER}" build \
    --file "${SCRIPT_DIR}/Dockerfile.spring-voyage-agents" \
    --build-arg "AGENT_BASE_IMAGE=${AGENT_BASE_OVERRIDE}" \
    --tag "${AGENTS_IMAGE}:${TAG}" \
    "${REPO_ROOT}"

log "built four agent images at tag :${TAG}"
log "  ${AGENT_BASE_IMAGE}:${TAG}"
log "  ${CLAUDE_IMAGE}:${TAG}"
log "  ${DAPR_IMAGE}:${TAG}"
log "  ${AGENTS_IMAGE}:${TAG}"
