#!/usr/bin/env bash
# deployment/build-agent-images.sh — single entry point for building every
# agent image the Spring Voyage dispatcher launches today (PR 3b of #1087,
# #1096; OSS role images added in #1536).
#
# Builds seven images, in dependency order:
#   1. ghcr.io/cvoya-com/spring-voyage-agent-base:<tag>  (path-1 BYOI base)
#   2. ghcr.io/cvoya-com/claude-code-base:<tag>           (path-1 reference, FROMs #1)
#   3. ghcr.io/cvoya-com/spring-voyage-agent:<tag>        (path-3 native A2A)
#   4. ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering:<tag>  (FROMs #1)
#   5. ghcr.io/cvoya-com/spring-voyage-agent-oss-design:<tag>                (FROMs #1)
#   6. ghcr.io/cvoya-com/spring-voyage-agent-oss-product-management:<tag>    (FROMs #1)
#   7. ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management:<tag>    (FROMs #1)
#
# Conformance paths are documented in
# `docs/architecture/agent-runtime.md` § 7. The ghcr-namespaced images are
# the same artifacts the `release.yml`, `release-agent-base.yml`, and
# `release-oss-agent-images.yml` workflows publish on tag push. The GHCR tags
# intentionally exist in the local image store before GHCR publishing is
# enabled: the runtime catalogue uses those canonical refs, and the dispatcher
# checks the exact configured ref with `image inspect` before trying a network
# pull.
#
# Usage:
#   deployment/build-agent-images.sh                # builds :dev tags
#   deployment/build-agent-images.sh --tag 1.2.3    # builds :1.2.3 tags
#   deployment/build-agent-images.sh --help
#
# Environment overrides (see --help for the full list):
#   DOCKER              — `docker` (default) or `podman`. Auto-detects if unset.
#   AGENT_BASE_IMAGE    — pin a published agent-base tag for the claude-code
#                         and OSS role builds instead of the locally-built
#                         one. Lets CI verify the published image without
#                         rebuilding.
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
TAG_LOCAL_ALIASES=1
AGENT_BASE_OVERRIDE="${AGENT_BASE_IMAGE:-}"

usage() {
    cat <<EOF
Usage: deployment/build-agent-images.sh [options]

Builds, in order:
  1. ghcr.io/cvoya-com/spring-voyage-agent-base:<tag>
  2. ghcr.io/cvoya-com/claude-code-base:<tag>
  3. ghcr.io/cvoya-com/spring-voyage-agent:<tag>
  4. ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering:<tag>  (FROMs #1)
  5. ghcr.io/cvoya-com/spring-voyage-agent-oss-design:<tag>                (FROMs #1)
  6. ghcr.io/cvoya-com/spring-voyage-agent-oss-product-management:<tag>    (FROMs #1)
  7. ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management:<tag>    (FROMs #1)

Options:
  --tag <value>                Tag suffix for all images (default: dev).
  --skip-agent-base            Skip building spring-voyage-agent-base:<tag>.
                               Useful when --agent-base-image points at an
                               already-pulled / already-built reference.
  --skip-oss                   Skip building the four OSS role images (steps
                               4-7). Still builds the existing three (steps 1-3).
  --ghcr-only                  Tag only canonical ghcr.io/... image refs.
                               By default the script also writes localhost/...
                               aliases for older local dev workflows.
  --push                       After building each ghcr.io/... image, also
                               push it to the registry. localhost/... aliases
                               are never pushed.
  --agent-base-image <ref>     Override the FROM line of the claude-code and
                               OSS role images. Defaults to
                               ghcr.io/cvoya-com/spring-voyage-agent-base:<tag>
                               (the tag built in step 1). Honors the
                               AGENT_BASE_IMAGE env var.
  -h, --help                   Show this help.

Environment:
  DOCKER                       Container CLI to use. Defaults to 'docker' if
                               on PATH, else 'podman'. Set explicitly to
                               force one runtime over the other.
  AGENT_BASE_IMAGE             Pre-seeds --agent-base-image.

Examples:
  # Local dev, all seven images at :dev:
  deployment/build-agent-images.sh

  # Verify the published agent-base image works:
  deployment/build-agent-images.sh --skip-agent-base \\
                                   --agent-base-image ghcr.io/cvoya-com/spring-voyage-agent-base:1.0.0

  # Build and push all seven images to GHCR:
  deployment/build-agent-images.sh --tag 1.2.3 --push

  # Skip the OSS role images and only build the base three:
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
        --ghcr-only)
            TAG_LOCAL_ALIASES=0
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
CLAUDE_IMAGE="ghcr.io/cvoya-com/claude-code-base"
CLAUDE_LOCAL_ALIAS="localhost/spring-voyage-agent-claude-code"
SV_AGENT_IMAGE="ghcr.io/cvoya-com/spring-voyage-agent"
SV_AGENT_LOCAL_ALIAS="localhost/spring-voyage-agent"
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

# Default the claude-code and OSS role FROM to whatever we just built (or
# to the user's pinned override). This is what makes the script work both
# online (CI verifying the published image) and offline (laptop without
# GHCR access).
if [[ -z "${AGENT_BASE_OVERRIDE}" ]]; then
    AGENT_BASE_OVERRIDE="${AGENT_BASE_IMAGE}:${TAG}"
fi

# ---- 2. claude-code-base (path 1) ----------------------------------------
claude_tags=(--tag "${CLAUDE_IMAGE}:${TAG}")
if [[ "${TAG_LOCAL_ALIASES}" -eq 1 ]]; then
    claude_tags+=(--tag "${CLAUDE_LOCAL_ALIAS}:${TAG}")
fi

log "building ${CLAUDE_IMAGE}:${TAG} (FROM ${AGENT_BASE_OVERRIDE})"
"${DOCKER}" build \
    --file "${SCRIPT_DIR}/Dockerfile.agent.claude-code" \
    --build-arg "AGENT_BASE_IMAGE=${AGENT_BASE_OVERRIDE}" \
    "${claude_tags[@]}" \
    "${REPO_ROOT}"
maybe_push "${CLAUDE_IMAGE}:${TAG}"

# ---- 3. spring-voyage-agent (path 3 — native A2A) -------------------------
sv_agent_tags=(--tag "${SV_AGENT_IMAGE}:${TAG}")
if [[ "${TAG_LOCAL_ALIASES}" -eq 1 ]]; then
    sv_agent_tags+=(--tag "${SV_AGENT_LOCAL_ALIAS}:${TAG}")
fi

log "building ${SV_AGENT_IMAGE}:${TAG}"
"${DOCKER}" build \
    --file "${SCRIPT_DIR}/Dockerfile.agent.dapr" \
    "${sv_agent_tags[@]}" \
    "${REPO_ROOT}"
maybe_push "${SV_AGENT_IMAGE}:${TAG}"

if [[ "${SKIP_OSS}" -eq 1 ]]; then
    log "skipping OSS role image builds (--skip-oss)"
else
    # ---- 4. OSS software-engineering agent -------------------------------
    log "building ${OSS_SE_IMAGE}:${TAG} (FROM ${AGENT_BASE_OVERRIDE})"
    "${DOCKER}" build \
        --file "${SCRIPT_DIR}/Dockerfile.agent.oss-software-engineering" \
        --build-arg "AGENT_BASE_IMAGE=${AGENT_BASE_OVERRIDE}" \
        --tag "${OSS_SE_IMAGE}:${TAG}" \
        "${REPO_ROOT}"
    maybe_push "${OSS_SE_IMAGE}:${TAG}"

    # ---- 5. OSS design agent ---------------------------------------------
    log "building ${OSS_DESIGN_IMAGE}:${TAG} (FROM ${AGENT_BASE_OVERRIDE})"
    "${DOCKER}" build \
        --file "${SCRIPT_DIR}/Dockerfile.agent.oss-design" \
        --build-arg "AGENT_BASE_IMAGE=${AGENT_BASE_OVERRIDE}" \
        --tag "${OSS_DESIGN_IMAGE}:${TAG}" \
        "${REPO_ROOT}"
    maybe_push "${OSS_DESIGN_IMAGE}:${TAG}"

    # ---- 6. OSS product-management agent ---------------------------------
    log "building ${OSS_PM_IMAGE}:${TAG} (FROM ${AGENT_BASE_OVERRIDE})"
    "${DOCKER}" build \
        --file "${SCRIPT_DIR}/Dockerfile.agent.oss-product-management" \
        --build-arg "AGENT_BASE_IMAGE=${AGENT_BASE_OVERRIDE}" \
        --tag "${OSS_PM_IMAGE}:${TAG}" \
        "${REPO_ROOT}"
    maybe_push "${OSS_PM_IMAGE}:${TAG}"

    # ---- 7. OSS program-management agent ---------------------------------
    log "building ${OSS_PGMGMT_IMAGE}:${TAG} (FROM ${AGENT_BASE_OVERRIDE})"
    "${DOCKER}" build \
        --file "${SCRIPT_DIR}/Dockerfile.agent.oss-program-management" \
        --build-arg "AGENT_BASE_IMAGE=${AGENT_BASE_OVERRIDE}" \
        --tag "${OSS_PGMGMT_IMAGE}:${TAG}" \
        "${REPO_ROOT}"
    maybe_push "${OSS_PGMGMT_IMAGE}:${TAG}"
fi

log "built agent images at tag :${TAG}"
log "  ${AGENT_BASE_IMAGE}:${TAG}"
log "  ${CLAUDE_IMAGE}:${TAG}"
log "  ${SV_AGENT_IMAGE}:${TAG}"
if [[ "${TAG_LOCAL_ALIASES}" -eq 1 ]]; then
    log "  ${CLAUDE_LOCAL_ALIAS}:${TAG} (local alias)"
    log "  ${SV_AGENT_LOCAL_ALIAS}:${TAG} (local alias)"
fi
if [[ "${SKIP_OSS}" -eq 0 ]]; then
    log "  ${OSS_SE_IMAGE}:${TAG}"
    log "  ${OSS_DESIGN_IMAGE}:${TAG}"
    log "  ${OSS_PM_IMAGE}:${TAG}"
    log "  ${OSS_PGMGMT_IMAGE}:${TAG}"
fi
