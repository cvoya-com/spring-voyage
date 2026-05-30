#!/usr/bin/env bash
# eng/release/release.sh — Spring Voyage release orchestration
#
# Publishes a coherent Spring Voyage release by pushing a single
# `spring-voyage-v<version>` tag and watching the unified `release.yml`
# workflow to completion. The previous three-tag release chain was
# collapsed into one tag and one workflow in #2229 (Path B); this script
# is the operator entry point for that flow.
#
# Usage:
#   ./eng/release/release.sh [OPTIONS] <semver>
#
# Arguments:
#   <semver>   Base semantic version, e.g. v1.0.0 or 1.0.0 (leading v optional).
#              Must match vMAJOR.MINOR.PATCH (no pre-release suffix here).
#
# Options:
#   --pre <alpha|beta|rc>   Append a date-anchored pre-release suffix:
#                           -<suffix>.YYYYMMDD  (same-day re-runs add .1, .2, …)
#   --plan                  Dry-run: print the computed tag and exit 0 without pushing.
#   --force-retag           Skip the idempotency guard (re-tag an existing version).
#   -h, --help              Show this help and exit.
#
# Examples:
#   ./eng/release/release.sh v1.0.0 --pre alpha     →  spring-voyage-v1.0.0-alpha.20260504
#   ./eng/release/release.sh 1.0.0  --pre rc        →  spring-voyage-v1.0.0-rc.20260504
#   ./eng/release/release.sh v1.0.0                 →  spring-voyage-v1.0.0  (stable)
#   ./eng/release/release.sh v1.0.0 --pre alpha --plan
#
# Tag pushed:
#   spring-voyage-v<version>   →  release.yml  (single unified workflow)
#
# Verification:
#   After the workflow succeeds, greps packages/**/*.yaml for
#   `image: ghcr.io/cvoya-com/*` references and checks each is
#   anonymously pullable via `podman manifest inspect --no-creds`.
#
# Requirements:
#   - gh CLI authenticated (`gh auth status`) with repo + workflow scopes
#   - git remote `origin` pointing at cvoya-com/spring-voyage with push access
#   - podman (for anonymous-pull verification; skipped with --plan)
#   - Run from the repository root on a clean checkout of main.

set -euo pipefail

# `packages/**/*.yaml` is meant to walk every depth of `packages/`. Without
# globstar, bash treats `**` as `*` and the verification step only sees
# top-level `packages/*/package.yaml` files — silently missing image refs
# in nested templates / units. Enable globstar before the glob is expanded.
shopt -s globstar

# ── Constants ────────────────────────────────────────────────────────────────

REPO="cvoya-com/spring-voyage"
PACKAGES_GLOB="packages/**/*.yaml"
WORKFLOW_NAME="release.yml"

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
#
# Pre-release counter: increments the candidate `.N` suffix while the
# `spring-voyage-v<candidate>` tag exists either on the remote or locally.
# Under the unified tag scheme there is exactly one tag form to consult
# (vs. the three-form check the prior chain needed in #2229's earlier
# iteration).

TODAY="$(date -u +%Y%m%d)"

# Returns 0 (true) if `spring-voyage-v<arg>` exists either as a remote tag
# on origin or as a local tag in the current worktree.
tag_exists() {
  local v="$1"
  git ls-remote --exit-code --tags origin "spring-voyage-v${v}" >/dev/null 2>&1 && return 0
  git tag -l "spring-voyage-v${v}" | grep -q .
}

if [[ -n "$PRE_RELEASE" ]]; then
  FULL_SEMVER="${BASE_SEMVER}-${PRE_RELEASE}.${TODAY}"
  if [[ "$FORCE_RETAG" != "true" ]]; then
    CANDIDATE="${FULL_SEMVER}"
    COUNTER=1
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
RELEASE_TAG="spring-voyage-${RELEASE_VERSION}"

# ── Dry-run / --plan mode ─────────────────────────────────────────────────────

if [[ "$DRY_RUN" == "true" ]]; then
  echo "=== Release plan (dry-run — no tags will be pushed) ==="
  echo ""
  echo "  Full version : ${RELEASE_VERSION}"
  echo "  Tag to push  : ${RELEASE_TAG}"
  echo "  Workflow     : ${WORKFLOW_NAME}"
  echo ""
  echo "  Step 1  push tag  ${RELEASE_TAG}"
  echo "          wait for  ${WORKFLOW_NAME}"
  echo ""
  echo "  Step 2  verify anonymous pull for all ghcr.io/cvoya-com/* images"
  echo "          referenced in packages/**/*.yaml"
  exit 0
fi

# ── HEAD must be on origin/main ──────────────────────────────────────────────
#
# Tags point to HEAD. If HEAD is not reachable from origin/main, releasing
# would tag commits that aren't in the canonical history — and `git push
# origin <tag>` would ship those unreviewed commits to the remote alongside
# the tag, bypassing branch protection on main. Fail fast here.

git fetch origin main --quiet 2>/dev/null || true
LOCAL_SHA="$(git rev-parse HEAD)"
ORIGIN_SHA="$(git rev-parse origin/main 2>/dev/null || true)"
if [[ -z "$ORIGIN_SHA" ]] || ! git merge-base --is-ancestor "${LOCAL_SHA}" origin/main 2>/dev/null; then
  echo "::error::HEAD (${LOCAL_SHA:0:12}) is not on origin/main."
  echo "         Push your commits to origin/main (via PR or direct push) before releasing."
  exit 1
fi

# ── Local main behind origin/main? ───────────────────────────────────────────
#
# The ancestor check above PASSES when HEAD is merely BEHIND origin/main (HEAD
# is still reachable from it). Tagging then cuts the release from a stale commit,
# silently omitting changes already merged to origin/main — e.g. a fix you
# merged via PR but haven't pulled locally. (This bit us: a release was cut from
# a local main that was behind, shipping without a just-merged fix.) Surface the
# gap and let the operator choose: sync to origin/main, or deliberately release
# the current commit.

BEHIND_COUNT="$(git rev-list --count "${LOCAL_SHA}..origin/main" 2>/dev/null || echo 0)"
if [[ "${BEHIND_COUNT}" -gt 0 ]]; then
  echo ""
  echo "⚠  Local HEAD is ${BEHIND_COUNT} commit(s) behind origin/main."
  echo "     HEAD         ${LOCAL_SHA:0:12}  $(git log -1 --format=%s "${LOCAL_SHA}" 2>/dev/null)"
  echo "     origin/main  ${ORIGIN_SHA:0:12}  $(git log -1 --format=%s origin/main 2>/dev/null)"
  echo ""
  echo "   Releasing now tags ${LOCAL_SHA:0:12} and OMITS these commits already on origin/main:"
  git log --oneline "${LOCAL_SHA}..origin/main" 2>/dev/null | sed 's/^/       /'
  echo ""

  if [[ -t 0 ]] || [[ -r /dev/tty ]]; then
    REPLY_SYNC=""
    printf '   (s)ync to origin/main and release that, (p)roceed on current HEAD, or (a)bort? [s/p/a] '
    if [[ -t 0 ]]; then
      IFS= read -r REPLY_SYNC || REPLY_SYNC=""
    else
      IFS= read -r REPLY_SYNC </dev/tty || REPLY_SYNC=""
    fi
    case "${REPLY_SYNC}" in
      [Ss]*)
        echo "   Fast-forwarding local main to origin/main …"
        if ! git merge --ff-only origin/main; then
          echo "::error::Could not fast-forward to origin/main (uncommitted changes, or not on main?)."
          echo "         Resolve with 'git pull --ff-only', then rerun."
          exit 1
        fi
        LOCAL_SHA="$(git rev-parse HEAD)"
        echo "   Synced — now at ${LOCAL_SHA:0:12}; releasing from origin/main."
        ;;
      [Pp]*)
        echo "   Proceeding on current HEAD (${LOCAL_SHA:0:12}); the commits above will NOT be in this release."
        ;;
      *)
        echo "   Aborted. Run 'git pull --ff-only' to sync, then rerun (or choose 'p' to release this commit deliberately)."
        exit 1
        ;;
    esac
  else
    echo "::error::Local main is behind origin/main and there is no terminal to confirm intent."
    echo "         Sync with 'git pull --ff-only origin main' and rerun, or run interactively to choose."
    exit 1
  fi
fi

# ── Local/remote tag divergence check ────────────────────────────────────────
#
# A local tag with no matching remote tag is unexpected state — it means a
# previous run was interrupted after `git tag` but before `git push`, or the
# remote tag was deleted without cleaning up locally. Fail hard here so the
# operator can decide: delete the local tag (`git tag -d <tag>`) and reuse
# this version, or start fresh with a new version.

if git tag -l "${RELEASE_TAG}" | grep -q . &&
   ! git ls-remote --exit-code --tags origin "${RELEASE_TAG}" >/dev/null 2>&1; then
  echo "::error::Local tag '${RELEASE_TAG}' exists but is not on origin."
  echo ""
  echo "         The remote tag was likely deleted without cleaning up locally."
  echo "         Delete it and rerun:"
  echo "           git tag -d ${RELEASE_TAG}"
  exit 1
fi

# ── Idempotency guard (stable releases only — pre-release handled above) ──────

if [[ -z "$PRE_RELEASE" && "$FORCE_RETAG" != "true" ]]; then
  if git ls-remote --exit-code --tags origin "${RELEASE_TAG}" >/dev/null 2>&1; then
    echo "::error::Tag '${RELEASE_TAG}' already exists on origin."
    echo "         Use --force-retag to override (this will re-trigger the workflow)."
    exit 1
  fi
fi

# ── Helper: push the tag and wait for the triggered workflow ──────────────────

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

push_and_wait "${RELEASE_TAG}" "${WORKFLOW_NAME}"

# ── Anonymous-pull verification ───────────────────────────────────────────────

echo ""
echo "▶  Verifying anonymous pull for all package-referenced images …"

# Strip leading v for the image tag (registry convention: 1.0.0 not v1.0.0).
IMAGE_TAG="${FULL_SEMVER}"

# Collect unique base image refs from packages (strip :tag suffix). The
# `shopt -s globstar` at the top of the script makes `**` walk every depth;
# `${PACKAGES_GLOB}` MUST stay unquoted so that glob actually expands.
mapfile -t IMAGES < <(
  # shellcheck disable=SC2086  # intentional glob expansion (see comment above)
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
