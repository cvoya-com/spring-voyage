#!/usr/bin/env bash
# devops/release/release.sh — Spring Voyage release orchestration
#
# Publishes a coherent Spring Voyage release by pushing component tags in
# dependency order and waiting for each workflow to succeed before proceeding.
#
# Usage:
#   ./devops/release/release.sh [OPTIONS] <semver>
#
# Arguments:
#   <semver>   Base semantic version, e.g. v1.0.0 or 1.0.0 (leading v optional).
#              Must match vMAJOR.MINOR.PATCH (no pre-release suffix here).
#
# Options:
#   --pre <alpha|beta|rc>   Append a date-anchored pre-release suffix:
#                           -<suffix>.YYYYMMDD  (same-day re-runs add .1, .2, …)
#   --plan                  Dry-run: print computed tags and exit 0 without pushing.
#   --force-retag           Skip the idempotency guard (re-tag an existing version).
#   -h, --help              Show this help and exit.
#
# Examples:
#   ./devops/release/release.sh v1.0.0 --pre alpha     →  v1.0.0-alpha.20260504
#   ./devops/release/release.sh 1.0.0  --pre rc        →  v1.0.0-rc.20260504
#   ./devops/release/release.sh v1.0.0                 →  v1.0.0  (stable)
#   ./devops/release/release.sh v1.0.0 --pre alpha --plan
#
# Tag chain pushed (in order, each waited on before the next):
#   agent-base-v<version>   →  release-agent-base.yml
#   oss-agents-v<version>   →  release-oss-agent-images.yml
#   v<version>              →  release.yml  (platform + GitHub Release)
#
# Verification:
#   After all workflows succeed, greps packages/**/*.yaml for
#   `image: ghcr.io/cvoya-com/*` references and checks each is
#   anonymously pullable via `podman manifest inspect --no-creds`.
#
# Requirements:
#   - gh CLI authenticated (`gh auth status`) with repo + workflow scopes
#   - git remote `origin` pointing at cvoya-com/spring-voyage with push access
#   - podman (for anonymous-pull verification; skipped with --plan)
#   - Run from the repository root on a clean checkout of main.

set -euo pipefail

# ── Constants ────────────────────────────────────────────────────────────────

REPO="cvoya-com/spring-voyage"
PACKAGES_GLOB="packages/**/*.yaml"

# ── Parse arguments ──────────────────────────────────────────────────────────

PRE_RELEASE=""
DRY_RUN=false
FORCE_RETAG=false
BASE_SEMVER=""

usage() {
  grep '^#' "$0" | grep -v '^#!/' | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --pre)
      [[ $# -lt 2 ]] && { echo "::error::--pre requires an argument (alpha|beta|rc)"; exit 1; }
      PRE_RELEASE="$2"
      if [[ ! "$PRE_RELEASE" =~ ^(alpha|beta|rc)$ ]]; then
        echo "::error::--pre value must be alpha, beta, or rc; got '$PRE_RELEASE'"
        exit 1
      fi
      shift 2
      ;;
    --plan)
      DRY_RUN=true
      shift
      ;;
    --force-retag)
      FORCE_RETAG=true
      shift
      ;;
    -h|--help)
      usage 0
      ;;
    -*)
      echo "::error::Unknown option '$1'. Run with --help for usage."
      exit 1
      ;;
    *)
      if [[ -n "$BASE_SEMVER" ]]; then
        echo "::error::Unexpected extra argument '$1'. Run with --help for usage."
        exit 1
      fi
      BASE_SEMVER="$1"
      shift
      ;;
  esac
done

if [[ -z "$BASE_SEMVER" ]]; then
  echo "::error::Missing required <semver> argument."
  usage 1
fi

# Normalize: strip leading v, validate MAJOR.MINOR.PATCH
BASE_SEMVER="${BASE_SEMVER#v}"
if ! [[ "$BASE_SEMVER" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
  echo "::error::Invalid semver '$BASE_SEMVER'. Expected MAJOR.MINOR.PATCH (e.g. 1.0.0)."
  exit 1
fi

# ── Compute full version ──────────────────────────────────────────────────────

TODAY="$(date -u +%Y%m%d)"

if [[ -n "$PRE_RELEASE" ]]; then
  # Build the candidate tag; auto-increment if it already exists (and no --force-retag).
  FULL_SEMVER="${BASE_SEMVER}-${PRE_RELEASE}.${TODAY}"
  if [[ "$FORCE_RETAG" != "true" ]]; then
    CANDIDATE="${FULL_SEMVER}"
    COUNTER=1
    tag_exists() {
      gh api "repos/${REPO}/git/refs/tags/agent-base-v${1}" --silent 2>/dev/null ||
      gh api "repos/${REPO}/git/refs/tags/oss-agents-v${1}" --silent 2>/dev/null ||
      gh api "repos/${REPO}/git/refs/tags/v${1}" --silent 2>/dev/null
    }
    while tag_exists "${CANDIDATE}"; do
      CANDIDATE="${FULL_SEMVER}.${COUNTER}"
      COUNTER=$((COUNTER + 1))
    done
    FULL_SEMVER="${CANDIDATE}"
  fi
else
  FULL_SEMVER="${BASE_SEMVER}"
fi

RELEASE_VERSION="v${FULL_SEMVER}"

# Compute the three component tags.
TAG_AGENT_BASE="agent-base-${RELEASE_VERSION}"
TAG_OSS_AGENTS="oss-agents-${RELEASE_VERSION}"
TAG_PLATFORM="${RELEASE_VERSION}"

# ── Dry-run / --plan mode ─────────────────────────────────────────────────────

if [[ "$DRY_RUN" == "true" ]]; then
  echo "=== Release plan (dry-run — no tags will be pushed) ==="
  echo ""
  echo "  Full version : ${RELEASE_VERSION}"
  echo ""
  echo "  Step 1  push tag  ${TAG_AGENT_BASE}"
  echo "          wait for  release-agent-base.yml"
  echo ""
  echo "  Step 2  push tag  ${TAG_OSS_AGENTS}"
  echo "          wait for  release-oss-agent-images.yml"
  echo ""
  echo "  Step 3  push tag  ${TAG_PLATFORM}"
  echo "          wait for  release.yml"
  echo ""
  echo "  Step 4  verify anonymous pull for all ghcr.io/cvoya-com/* images"
  echo "          referenced in packages/**/*.yaml"
  exit 0
fi

# ── Local/remote tag divergence check ────────────────────────────────────────
#
# A local tag that has no matching remote tag is unexpected state — it means
# a previous run was interrupted after `git tag` but before `git push`, or
# the remote tag was deleted without cleaning up locally.  We fail hard here
# so the operator can decide: delete the local tag (`git tag -d <tag>`) and
# reuse this version, or start fresh with a new version.

STALE_LOCAL_TAGS=()
for tag in "$TAG_AGENT_BASE" "$TAG_OSS_AGENTS" "$TAG_PLATFORM"; do
  if git tag -l "${tag}" | grep -q . &&
     ! gh api "repos/${REPO}/git/refs/tags/${tag}" --silent 2>/dev/null; then
    STALE_LOCAL_TAGS+=("${tag}")
  fi
done
if [[ ${#STALE_LOCAL_TAGS[@]} -gt 0 ]]; then
  echo "::error::The following local tags exist but are not on ${REPO}:"
  for tag in "${STALE_LOCAL_TAGS[@]}"; do
    echo "           ${tag}"
  done
  echo ""
  echo "         Remote tags were likely deleted without cleaning up locally."
  echo "         Delete them and rerun:"
  echo "           git tag -d ${STALE_LOCAL_TAGS[*]}"
  exit 1
fi

# ── Idempotency guard (stable releases only — pre-release handled above) ──────

if [[ -z "$PRE_RELEASE" && "$FORCE_RETAG" != "true" ]]; then
  for tag in "$TAG_AGENT_BASE" "$TAG_OSS_AGENTS" "$TAG_PLATFORM"; do
    if gh api "repos/${REPO}/git/refs/tags/${tag}" --silent 2>/dev/null; then
      echo "::error::Tag '${tag}' already exists in ${REPO}."
      echo "         Use --force-retag to override (this will re-trigger the workflow)."
      exit 1
    fi
  done
fi

# ── Helper: push a tag and wait for the triggered workflow ────────────────────

push_and_wait() {
  local tag="$1"
  local workflow_name="$2"

  echo ""
  echo "▶  Pushing tag ${tag} …"
  git tag "${tag}"
  git push origin "${tag}"

  echo "   Waiting for workflow '${workflow_name}' to register …"
  local run_id=""
  local attempt=0
  while [[ -z "$run_id" || "$run_id" == "null" ]]; do
    attempt=$((attempt + 1))
    if (( attempt > 36 )); then
      echo "::error::Timed out waiting for workflow '${workflow_name}' to start for tag '${tag}' (3 min)"
      exit 1
    fi
    sleep 5
    run_id="$(gh run list \
      --repo "${REPO}" \
      --workflow "${workflow_name}" \
      --branch "${tag}" \
      --limit 1 \
      --json databaseId \
      --jq '.[0].databaseId' 2>/dev/null || true)"
  done

  echo "   Watching run ${run_id} …"
  gh run watch \
    --repo "${REPO}" \
    --exit-status \
    "${run_id}"

  echo "✓  ${workflow_name} succeeded."
}

# ── Main release sequence ─────────────────────────────────────────────────────

echo ""
echo "╔══════════════════════════════════════════════════════════════════╗"
echo "║  Spring Voyage release: ${RELEASE_VERSION}"
echo "╚══════════════════════════════════════════════════════════════════╝"

push_and_wait "${TAG_AGENT_BASE}"   "release-agent-base.yml"
push_and_wait "${TAG_OSS_AGENTS}"   "release-oss-agent-images.yml"
push_and_wait "${TAG_PLATFORM}"     "release.yml"

# ── Anonymous-pull verification ───────────────────────────────────────────────

echo ""
echo "▶  Verifying anonymous pull for all package-referenced images …"

# Strip leading v for the image tag (registry convention: 1.0.0 not v1.0.0).
IMAGE_TAG="${FULL_SEMVER}"

# Collect unique base image refs from packages (strip :tag suffix).
mapfile -t IMAGES < <(
  grep -rh 'image: ghcr.io/cvoya-com/' ${PACKAGES_GLOB} 2>/dev/null \
    | sed 's/.*image: //' \
    | sed 's/:.*//' \
    | sort -u
)

if [[ ${#IMAGES[@]} -eq 0 ]]; then
  echo "   No ghcr.io/cvoya-com/* image references found in packages — skipping."
else
  FAILED=()
  for base in "${IMAGES[@]}"; do
    ref="${base}:${IMAGE_TAG}"
    echo -n "   ${ref} … "
    if podman manifest inspect --no-creds "${ref}" > /dev/null 2>&1; then
      echo "✓"
    else
      echo "✗  FAIL"
      FAILED+=("${ref}")
    fi
  done

  if [[ ${#FAILED[@]} -gt 0 ]]; then
    echo ""
    echo "::error::Anonymous pull failed for the following images:"
    for f in "${FAILED[@]}"; do
      echo "         ${f}"
    done
    echo ""
    echo "         Check that each GHCR package is set to public visibility."
    exit 1
  fi
fi

echo ""
echo "╔══════════════════════════════════════════════════════════════════╗"
echo "║  Release ${RELEASE_VERSION} complete. All images are publicly pullable."
echo "╚══════════════════════════════════════════════════════════════════╝"
echo ""
