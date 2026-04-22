#!/usr/bin/env bash
# Build the agent-base image locally for development. The published
# image (ghcr.io/cvoya/agent-base) is built by the
# release-agent-base.yml CI workflow on tag push; this script is the
# escape hatch for hacking on the bridge from a developer laptop.
#
# Usage:
#   deployment/build-sidecar.sh             # builds :dev
#   deployment/build-sidecar.sh v1.0.0      # builds :v1.0.0 + :1.0.0

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

TAG="${1:-dev}"
IMAGE="ghcr.io/cvoya/agent-base"
DOCKERFILE="${SCRIPT_DIR}/Dockerfile.agent-base"

if [[ ! -f "${DOCKERFILE}" ]]; then
    echo "::error::Dockerfile not found at ${DOCKERFILE}" >&2
    exit 1
fi

# Run the unit tests before we bake anything. The sidecar is small
# enough that the test suite finishes in seconds, and a busted bridge
# inside the published image is a rotten user experience.
pushd "${SCRIPT_DIR}/agent-sidecar" >/dev/null
if command -v npm >/dev/null 2>&1; then
    npm install --no-audit --no-fund
    npm test
else
    echo "::warning::npm not found on PATH; skipping sidecar unit tests" >&2
fi
popd >/dev/null

declare -a TAG_ARGS
TAG_ARGS+=("--tag" "${IMAGE}:${TAG}")

# When the user passes a v-prefixed semver, also publish the bare
# version tag so consumers can pin either form.
if [[ "${TAG}" =~ ^v([0-9]+\.[0-9]+\.[0-9]+(-.*)?)$ ]]; then
    BARE="${BASH_REMATCH[1]}"
    TAG_ARGS+=("--tag" "${IMAGE}:${BARE}")
fi

echo "Building ${IMAGE}:${TAG}"
docker build \
    --file "${DOCKERFILE}" \
    "${TAG_ARGS[@]}" \
    "${REPO_ROOT}"

echo "Built ${IMAGE}:${TAG}. Tags:"
printf '  %s\n' "${TAG_ARGS[@]:1:1}"
if [[ ${#TAG_ARGS[@]} -gt 2 ]]; then
    printf '  %s\n' "${TAG_ARGS[@]:3:1}"
fi
