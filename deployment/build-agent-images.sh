#!/usr/bin/env bash
# deployment/build-agent-images.sh — single entry point for building every
# agent image the Spring Voyage dispatcher launches today (PR 3b of #1087,
# #1096; omnibus added in #1514; OSS role images added in #1536).
#
# Builds eight images, in dependency order:
#   1. ghcr.io/cvoya-com/spring-voyage-agent-base:<tag>  (path-1 BYOI base)
#   2. localhost/spring-voyage-agent-claude-code:<tag>   (path-1 reference, FROMs #1)
#   3. localhost/spring-voyage-agent:<tag>          (path-3 native A2A)
#   4. ghcr.io/cvoya-com/spring-voyage-agents:<tag>      (omnibus default, FROMs #1)
#   5. ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering:<tag>  (FROMs #4)
#   6. ghcr.io/cvoya-com/spring-voyage-agent-oss-design:<tag>                (FROMs #4)
#   7. ghcr.io/cvoya-com/spring-voyage-agent-oss-product-management:<tag>    (FROMs #4)
#   8. ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management:<tag>    (FROMs #4)
#
# Conformance paths are documented in
# `docs/architecture/agent-runtime.md` § 7. The ghcr-namespaced images are
# the same artifacts the `release-agent-base.yml`,
# `release-spring-voyage-agents.yml`, and `release-oss-agent-images.yml`
# workflows publish on tag push — building locally here is the offline
# fallback for laptops + CI runs without GHCR pull access.
#
# Usage:
#   deployment/build-agent-images.sh                # builds :dev tags
#   deployment/build-agent-images.sh --tag 1.2.3    # builds :1.2.3 tags
#   deployment/build-agent-images.sh --help
#
# Environment overrides (see --help for the full list):
#   DOCKER              — `docker` (default) or `podman`. Auto-detects if unset.
#   AGENT_BASE_IMAGE    — pin a published agent-base tag for the claude-code
#                         and omnibus builds instead of the locally-built one.
#                         Lets CI verify the published image without rebuilding.
#   AGENTS_OMNIBUS_IMAGE — pin a published omnibus tag for the OSS role image
#                          builds instead of the locally-built one.
#
# Mirrors the structure and style of `deployment/build-sidecar.sh` so an
# operator who knows one knows the other.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

TAG="dev"
SKIP_AGENT_BASE=0
SKIP_OSS=0
PUSH=0
AGENT_BASE_OVERRIDE="${AGENT_BASE_IMAGE:-}"
AGENTS_OMNIBUS_OVERRIDE="${AGENTS_OMNIBUS_IMAGE:-}"

usage() {
    cat <<EOF
Usage: deployment/build-agent-images.sh [options]

Builds, in order:
  1. ghcr.io/cvoya-com/spring-voyage-agent-base:<tag>
  2. localhost/spring-voyage-agent-claude-code:<tag>
  3. localhost/spring-voyage-agent:<tag>
  4. ghcr.io/cvoya-com/spring-voyage-agents:<tag>                          (omnibus)
  5. ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering:<tag>  (FROMs #4)
  6. ghcr.io/cvoya-com/spring-voyage-agent-oss-design:<tag>                (FROMs #4)
  7. ghcr.io/cvoya-com/spring-voyage-agent-oss-product-management:<tag>    (FROMs #4)
  8. ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management:<tag>    (FROMs #4)

Options:
  --tag <value>                Tag suffix for all images (default: dev).
  --skip-agent-base            Skip building spring-voyage-agent-base:<tag>.
                               Useful when --agent-base-image points at an
                               already-pulled / already-built reference.
  --skip-oss                   Skip building the four OSS role images (steps
                               5-8). Still builds the existing four (steps 1-4).
  --push                       After building each ghcr.io/... image, also
                               push it to the registry. localhost/... images
                               are never pushed (they are locally-tagged by
                               design).
  --agent-base-image <ref>     Override the FROM line of the claude-code and
                               omnibus images. Defaults to
                               ghcr.io/cvoya-com/spring-voyage-agent-base:<tag>
                               (the tag built in step 1). Honors the
                               AGENT_BASE_IMAGE env var.
  --agents-omnibus-image <ref> Override the FROM line of the four OSS role
                               images. Defaults to
                               ghcr.io/cvoya-com/spring-voyage-agents:<tag>
                               (the tag built in step 4). Honors the
                               AGENTS_OMNIBUS_IMAGE env var.
  -h, --help                   Show this help.

Environment:
  DOCKER                       Container CLI to use. Defaults to 'docker' if
                               on PATH, else 'podman'. Set explicitly to
                               force one runtime over the other.
  AGENT_BASE_IMAGE             Pre-seeds --agent-base-image.
  AGENTS_OMNIBUS_IMAGE         Pre-seeds --agents-omnibus-image.

Examples:
  # Local dev, all eight images at :dev:
  deployment/build-agent-images.sh

  # Verify the published agent-base image works:
  deployment/build-agent-images.sh --skip-agent-base \\
                                   --agent-base-image ghcr.io/cvoya-com/spring-voyage-agent-base:1.0.0

  # Build and push all eight images to GHCR:
  deployment/build-agent-images.sh --tag 1.2.3 --push

  # Skip the OSS role images and only build the base four:
  deployment/build-agent-images.sh --skip-oss
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
        --skip-oss)
            SKIP_OSS=1
            shift
            ;;
        --push)
            PUSH=1
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
        --agents-omnibus-image)
            AGENTS_OMNIBUS_OVERRIDE="${2:?--agents-omnibus-image requires a value}"
            shift 2
            ;;
        --agents-omnibus-image=*)
            AGENTS_OMNIBUS_OVERRIDE="${1#*=}"
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
SV_AGENT_IMAGE="localhost/spring-voyage-agent"
AGENTS_IMAGE="ghcr.io/cvoya-com/spring-voyage-agents"
OSS_SE_IMAGE="ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering"
OSS_DESIGN_IMAGE="ghcr.io/cvoya-com/spring-voyage-agent-oss-design"
OSS_PM_IMAGE="ghcr.io/cvoya-com/spring-voyage-agent-oss-product-management"
OSS_PGMGMT_IMAGE="ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management"

# Helper: push a ghcr.io image if --push was requested.
# localhost/... images are skipped even with --push.
maybe_push() {
    local image_ref="$1"
    if [[ "${PUSH}" -eq 1 ]] && [[ "${image_ref}" == ghcr.io/* ]]; then
        log "pushing ${image_ref}"
        "${DOCKER}" push "${image_ref}"
    fi
}

# ---- 1. agent-base -------------------------------------------------------
if [[ "${SKIP_AGENT_BASE}" -eq 1 ]]; then
    log "skipping agent-base build (--skip-agent-base)"
else
    log "building ${AGENT_BASE_IMAGE}:${TAG}"
    "${DOCKER}" build \
        --file "${SCRIPT_DIR}/Dockerfile.agent-base" \
        --tag "${AGENT_BASE_IMAGE}:${TAG}" \
        "${REPO_ROOT}"
    maybe_push "${AGENT_BASE_IMAGE}:${TAG}"
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
log "building ${SV_AGENT_IMAGE}:${TAG}"
"${DOCKER}" build \
    --file "${SCRIPT_DIR}/Dockerfile.agent.dapr" \
    --tag "${SV_AGENT_IMAGE}:${TAG}" \
    "${REPO_ROOT}"

# ---- 4. spring-voyage-agents (omnibus default) ---------------------------
log "building ${AGENTS_IMAGE}:${TAG} (FROM ${AGENT_BASE_OVERRIDE})"
"${DOCKER}" build \
    --file "${SCRIPT_DIR}/Dockerfile.spring-voyage-agents" \
    --build-arg "AGENT_BASE_IMAGE=${AGENT_BASE_OVERRIDE}" \
    --tag "${AGENTS_IMAGE}:${TAG}" \
    "${REPO_ROOT}"
maybe_push "${AGENTS_IMAGE}:${TAG}"

# Default the OSS role image FROM to the omnibus we just built (or to the
# user's pinned override via --agents-omnibus-image / AGENTS_OMNIBUS_IMAGE).
if [[ -z "${AGENTS_OMNIBUS_OVERRIDE}" ]]; then
    AGENTS_OMNIBUS_OVERRIDE="${AGENTS_IMAGE}:${TAG}"
fi

if [[ "${SKIP_OSS}" -eq 1 ]]; then
    log "skipping OSS role image builds (--skip-oss)"
else
    # ---- 5. OSS software-engineering agent -------------------------------
    log "building ${OSS_SE_IMAGE}:${TAG} (FROM ${AGENTS_OMNIBUS_OVERRIDE})"
    "${DOCKER}" build \
        --file "${SCRIPT_DIR}/Dockerfile.agent.oss-software-engineering" \
        --build-arg "AGENT_BASE_IMAGE=${AGENTS_OMNIBUS_OVERRIDE}" \
        --tag "${OSS_SE_IMAGE}:${TAG}" \
        "${REPO_ROOT}"
    maybe_push "${OSS_SE_IMAGE}:${TAG}"

    # ---- 6. OSS design agent ---------------------------------------------
    log "building ${OSS_DESIGN_IMAGE}:${TAG} (FROM ${AGENTS_OMNIBUS_OVERRIDE})"
    "${DOCKER}" build \
        --file "${SCRIPT_DIR}/Dockerfile.agent.oss-design" \
        --build-arg "AGENT_BASE_IMAGE=${AGENTS_OMNIBUS_OVERRIDE}" \
        --tag "${OSS_DESIGN_IMAGE}:${TAG}" \
        "${REPO_ROOT}"
    maybe_push "${OSS_DESIGN_IMAGE}:${TAG}"

    # ---- 7. OSS product-management agent ---------------------------------
    log "building ${OSS_PM_IMAGE}:${TAG} (FROM ${AGENTS_OMNIBUS_OVERRIDE})"
    "${DOCKER}" build \
        --file "${SCRIPT_DIR}/Dockerfile.agent.oss-product-management" \
        --build-arg "AGENT_BASE_IMAGE=${AGENTS_OMNIBUS_OVERRIDE}" \
        --tag "${OSS_PM_IMAGE}:${TAG}" \
        "${REPO_ROOT}"
    maybe_push "${OSS_PM_IMAGE}:${TAG}"

    # ---- 8. OSS program-management agent ---------------------------------
    log "building ${OSS_PGMGMT_IMAGE}:${TAG} (FROM ${AGENTS_OMNIBUS_OVERRIDE})"
    "${DOCKER}" build \
        --file "${SCRIPT_DIR}/Dockerfile.agent.oss-program-management" \
        --build-arg "AGENT_BASE_IMAGE=${AGENTS_OMNIBUS_OVERRIDE}" \
        --tag "${OSS_PGMGMT_IMAGE}:${TAG}" \
        "${REPO_ROOT}"
    maybe_push "${OSS_PGMGMT_IMAGE}:${TAG}"
fi

log "built agent images at tag :${TAG}"
log "  ${AGENT_BASE_IMAGE}:${TAG}"
log "  ${CLAUDE_IMAGE}:${TAG}"
log "  ${SV_AGENT_IMAGE}:${TAG}"
log "  ${AGENTS_IMAGE}:${TAG}"
if [[ "${SKIP_OSS}" -eq 0 ]]; then
    log "  ${OSS_SE_IMAGE}:${TAG}"
    log "  ${OSS_DESIGN_IMAGE}:${TAG}"
    log "  ${OSS_PM_IMAGE}:${TAG}"
    log "  ${OSS_PGMGMT_IMAGE}:${TAG}"
fi
