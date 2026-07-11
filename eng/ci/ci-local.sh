#!/usr/bin/env bash
# Local pre-push gate — the fast subset of the CI static checks.
#
# Run automatically by the pre-push hook (.githooks/pre-push); install once with
# eng/install-hooks.sh. It mirrors the static-input CI jobs (Format check, Lint
# web, Typecheck web, Lint Python agents) plus a .NET build, scoped by change
# detection to the areas you touched, so an obvious failure is caught before it
# burns a PR / merge-queue cycle.
#
# The heavy / integration suites (dotnet test, web build/knip/e2e/lighthouse,
# pytest) run in CI on the PR and again in the merge queue — pass --full to also
# run them locally.
#
# Usage:
#   eng/ci/ci-local.sh [dotnet|web|python|workflows ...] [--all] [--full] [--skip-dotnet]
#
#   (no section)   auto-detect changed areas vs origin/main; falls back to all
#   --all          run every section regardless of detected changes
#   --full         also run tests / heavier suites (dotnet test, web build+knip+test, pytest)
#   --skip-dotnet  skip the .NET section (its build is the slow part)
#   -h, --help     show this help

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

if [ -t 1 ]; then
  RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[1;33m'; BLUE=$'\033[0;34m'; NC=$'\033[0m'
else
  RED=""; GREEN=""; YELLOW=""; BLUE=""; NC=""
fi
say()  { echo "${BLUE}[ci-local]${NC} $*"; }
ok()   { echo "${GREEN}[ok]${NC} $*"; }
warn() { echo "${YELLOW}[warn]${NC} $*"; }
err()  { echo "${RED}[fail]${NC} $*" >&2; }

FULL=0
FORCE_ALL=0
SKIP_DOTNET=0
SECTIONS=()

while [ $# -gt 0 ]; do
  case "$1" in
    --full)        FULL=1 ;;
    --all)         FORCE_ALL=1 ;;
    --skip-dotnet) SKIP_DOTNET=1 ;;
    dotnet|web|python|workflows) SECTIONS+=("$1") ;;
    -h|--help)     awk '/^# Local pre-push gate/{p=1} p&&!/^#/{exit} p&&/^#/{sub(/^# ?/,"");print}' "$0"; exit 0 ;;
    *) err "unknown argument: $1"; exit 2 ;;
  esac
  shift
done

detect_sections() {
  local base="origin/main"
  if ! git rev-parse --verify --quiet "${base}^{commit}" >/dev/null 2>&1; then
    echo "dotnet web python workflows"; return
  fi
  local files
  files="$( { git diff --name-only "${base}...HEAD" 2>/dev/null
              git diff --name-only HEAD 2>/dev/null
              git diff --name-only --cached 2>/dev/null; } | sort -u )"
  [ -n "$files" ] || { echo ""; return; }
  local d=0 w=0 p=0 gha=0 out=""
  printf '%s\n' "$files" | grep -qE '\.(cs|csproj|slnx|props)$|^global\.json$|^NuGet\.config$|^\.config/dotnet-tools\.json$' && d=1
  printf '%s\n' "$files" | grep -qE '^src/Cvoya\.Spring\.Web/|^src/Cvoya\.Spring\.Connector\..*/web/|^eslint\.config\.mjs$|^package(-lock)?\.json$' && w=1
  printf '%s\n' "$files" | grep -qE '^agents/(spring-voyage-agent|spring-voyage-agent-sdk|magazine-langgraph-orchestrator)/' && p=1
  printf '%s\n' "$files" | grep -qE '^\.github/workflows/|(^|/)Dockerfile$|(^|/)Dockerfile\.|(^|/)(package-lock\.json|packages\.lock\.json)$' && gha=1
  [ "$d" = 1 ] && out="$out dotnet"
  [ "$w" = 1 ] && out="$out web"
  [ "$p" = 1 ] && out="$out python"
  [ "$gha" = 1 ] && out="$out workflows"
  echo "$out" | xargs
}

run_dotnet() {
  [ "$SKIP_DOTNET" = 1 ] && { warn "dotnet: skipped (--skip-dotnet)"; return 0; }
  command -v dotnet >/dev/null 2>&1 || { err "dotnet not found"; return 1; }
  say "dotnet: restore"
  dotnet restore SpringVoyage.slnx || return 1
  dotnet tool restore || return 1
  # Build first: the CLI's GenerateKiotaClient target re-emits the gitignored
  # Generated/ tree that 'dotnet format' needs to analyse (matches CI).
  say "dotnet: build (Release; regenerates Kiota client)"
  dotnet build SpringVoyage.slnx --no-restore --configuration Release || return 1
  say "dotnet: format --verify-no-changes"
  dotnet format SpringVoyage.slnx --no-restore --verify-no-changes || return 1
  if [ "$FULL" = 1 ]; then
    say "dotnet: test (watchdog)"
    eng/ci/dotnet-test-with-watchdog.sh --solution SpringVoyage.slnx --no-restore --no-build --configuration Release || return 1
  fi
}

run_web() {
  command -v npm >/dev/null 2>&1 || { err "npm not found"; return 1; }
  [ -d node_modules ] || { say "web: npm ci"; npm ci || return 1; }
  say "web: lint (eslint)"
  npm run lint || return 1
  say "web: typecheck (tsc)"
  npm --workspace=spring-voyage-dashboard run typecheck || return 1
  if [ "$FULL" = 1 ]; then
    say "web: knip (dead code)"
    npm --workspace=spring-voyage-dashboard run knip || return 1
    say "web: build (next build)"
    ( cd src/Cvoya.Spring.Web && npm run build ) || return 1
    say "web: test (vitest)"
    npm --workspace=spring-voyage-dashboard run test || return 1
  fi
}

run_python() {
  if ! command -v ruff >/dev/null 2>&1; then
    warn "python: ruff not found — skipping (install with 'pip install ruff')"
    return 0
  fi
  local dirs="agents/spring-voyage-agent/ agents/spring-voyage-agent-sdk/ agents/magazine-langgraph-orchestrator/"
  say "python: ruff check"
  ruff check $dirs || return 1
  say "python: ruff format --check"
  ruff format --check $dirs || return 1
}

run_workflows() {
  say "workflows: zizmor"
  eng/ci/zizmor.sh || return 1
  say "workflows: trivy"
  eng/ci/trivy.sh || return 1
}

if [ "${#SECTIONS[@]}" -eq 0 ]; then
  if [ "$FORCE_ALL" = 1 ]; then
    SECTIONS=(dotnet web python workflows)
  else
    read -r -a SECTIONS <<<"$(detect_sections)"
  fi
fi

if [ "${#SECTIONS[@]}" -eq 0 ]; then
  ok "no relevant changes detected — nothing to check"
  exit 0
fi

say "running sections: ${SECTIONS[*]}${FULL:+ (full)}"
FAILED=()
for s in "${SECTIONS[@]}"; do
  case "$s" in
    dotnet) run_dotnet || FAILED+=("dotnet") ;;
    web)    run_web    || FAILED+=("web") ;;
    python) run_python || FAILED+=("python") ;;
    workflows) run_workflows || FAILED+=("workflows") ;;
    *) warn "unknown section: $s" ;;
  esac
done

echo
if [ "${#FAILED[@]}" -ne 0 ]; then
  err "FAILED: ${FAILED[*]}"
  err "Fix the above (e.g. '/format', 'dotnet format SpringVoyage.slnx', 'npm run lint:fix', 'ruff format agents/…') and re-run."
  exit 1
fi
ok "all checks passed"
