#!/usr/bin/env bash
# Spring Voyage — source-free local-host installer.
#
# Canonical operator entry-point (ADR-0042). Downloads the platform image,
# deployment bundle, dispatcher binary, and `spring` CLI from a GitHub
# Release, verifies them against the release's SHA256SUMS, generates a
# spring.env with reasonable defaults, and brings the stack up via
# `deploy.sh up`. Local-host only — no SSH, no remote container contexts.
#
# Usage:
#   curl -fSL https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh | bash
#
#   install.sh [--version <tag>] [--root <dir>] [--yes] [--force] [--no-start]
#
# Two prompts only:
#   1. DEPLOY_HOSTNAME (default `localhost`)
#   2. Configure GitHub App for this deployment? (Y/n)
#
# `--yes` skips both. Passwords, AES keys, and paths are auto-generated or
# auto-derived from the bundle's manifest.json. See ADR-0042 for the full
# decision record.

set -euo pipefail

# ---------------------------------------------------------------------------
# Constants & defaults
# ---------------------------------------------------------------------------
REPO_OWNER="cvoya-com"
REPO_NAME="spring-voyage"
RELEASE_API_LATEST="https://api.github.com/repos/${REPO_OWNER}/${REPO_NAME}/releases/latest"
RELEASE_DOWNLOAD_PREFIX="https://github.com/${REPO_OWNER}/${REPO_NAME}/releases/download"

DEFAULT_INSTALL_ROOT="${HOME}/.spring-voyage"
DEFAULT_BIN_DIR="${HOME}/.local/bin"

VERSION="${SPRING_VOYAGE_VERSION:-}"
INSTALL_ROOT="${SPRING_VOYAGE_HOME:-${DEFAULT_INSTALL_ROOT}}"
BIN_DIR="${SPRING_VOYAGE_BIN_DIR:-${DEFAULT_BIN_DIR}}"
ASSUME_YES=0
FORCE=0
NO_START=0

# ---------------------------------------------------------------------------
# Colour helpers (degrade gracefully when not a TTY)
# ---------------------------------------------------------------------------
if [[ -t 1 ]]; then
  BOLD=$'\033[1m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[1;33m'; RED=$'\033[0;31m'; CYAN=$'\033[0;36m'; NC=$'\033[0m'
else
  BOLD=''; GREEN=''; YELLOW=''; RED=''; CYAN=''; NC=''
fi

header() { printf "\n${BOLD}${CYAN}==> %s${NC}\n" "$1"; }
ok()     { printf "  ${GREEN}✓${NC}  %s\n" "$*"; }
info()   { printf "      %s\n" "$*"; }
warn()   { printf "  ${YELLOW}!${NC}  %s\n" "$*" >&2; }
fail()   { printf "\n  ${RED}✗${NC}  %s\n\n" "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Usage
# ---------------------------------------------------------------------------
usage() {
  cat <<'EOF'
Spring Voyage — source-free local-host installer.

Usage:
  install.sh [--version <tag>] [--root <dir>] [--yes] [--force] [--no-start]

Options:
  --version <tag>   Release tag to install (e.g. v1.0.0). Defaults to
                    $SPRING_VOYAGE_VERSION or the latest stable release.
  --root <dir>      Install root (default: ~/.spring-voyage).
  --yes             Non-interactive. Uses DEPLOY_HOSTNAME=localhost and
                    skips the GitHub App manifest flow.
  --force           Bypass the "already installed" refusal.
  --no-start        Generate spring.env and assets but do not invoke
                    `deploy.sh up`. Useful for CI bring-up that wants to
                    inspect spring.env first.
  -h, --help        Show this message.

Environment overrides:
  SPRING_VOYAGE_VERSION   Release tag (same as --version).
  SPRING_VOYAGE_HOME      Install root (same as --root).
EOF
}

# ---------------------------------------------------------------------------
# Arg parsing
# ---------------------------------------------------------------------------
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      [[ $# -ge 2 ]] || fail "--version requires an argument"
      VERSION="$2"; shift 2 ;;
    --version=*) VERSION="${1#*=}"; shift ;;
    --root)
      [[ $# -ge 2 ]] || fail "--root requires an argument"
      INSTALL_ROOT="$2"; shift 2 ;;
    --root=*) INSTALL_ROOT="${1#*=}"; shift ;;
    --yes|-y) ASSUME_YES=1; shift ;;
    --force) FORCE=1; shift ;;
    --no-start) NO_START=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) fail "unknown option: $1 (try --help)" ;;
  esac
done

# ---------------------------------------------------------------------------
# Pre-flight: never run as root
# ---------------------------------------------------------------------------
header "Pre-flight checks"

if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
  fail "Refusing to run as root. Re-run as your normal user; the installer creates files under \$HOME."
fi
ok "Running as non-root user"

# ---------------------------------------------------------------------------
# Pre-flight: tools
# ---------------------------------------------------------------------------
require_tool() {
  local tool="$1" hint="${2:-}"
  if ! command -v "$tool" >/dev/null 2>&1; then
    fail "Required tool '$tool' is not on PATH.${hint:+ }$hint"
  fi
}

# Bash 4+ required (associative arrays, `[[ ${var,,} ]]`).
if [[ -z "${BASH_VERSION:-}" ]]; then
  fail "bash is required to run this script."
fi
bash_major="${BASH_VERSION%%.*}"
if (( bash_major < 4 )); then
  fail "bash >= 4 required (found $BASH_VERSION). On macOS install via 'brew install bash' and rerun."
fi
ok "bash $BASH_VERSION"

require_tool curl    "Install curl via your system package manager and rerun."
require_tool tar     "Install tar via your system package manager and rerun."
require_tool openssl "Install openssl via your system package manager and rerun."
require_tool podman  "Install Podman 4+ (https://podman.io/) and rerun."

# Podman 4+ required (rootless networking, host.containers.internal).
podman_version="$(podman version --format '{{.Client.Version}}' 2>/dev/null || podman version | awk '/^Version:/{print $2; exit}')"
podman_major="${podman_version%%.*}"
if [[ -z "$podman_major" || "$podman_major" -lt 4 ]]; then
  fail "Podman >= 4.0 required (found ${podman_version:-unknown}). Upgrade Podman and rerun."
fi
ok "podman ${podman_version}"

# Optional: sha256sum or shasum (one must exist for SHA256SUMS verification).
if command -v sha256sum >/dev/null 2>&1; then
  SHA256_TOOL="sha256sum"
elif command -v shasum >/dev/null 2>&1; then
  SHA256_TOOL="shasum -a 256"
else
  fail "Neither sha256sum nor shasum found. Install GNU coreutils (Linux) or use macOS's built-in shasum."
fi
ok "sha256 verification tool: ${SHA256_TOOL}"

# ---------------------------------------------------------------------------
# Pre-flight: macOS podman machine state
# ---------------------------------------------------------------------------
case "$(uname -s)" in
  Darwin)
    # On macOS, `podman` needs a running VM (podman machine).
    if ! podman machine list --format '{{.Running}}' 2>/dev/null | grep -q '^true$'; then
      fail "No running Podman machine on macOS. Run 'podman machine init && podman machine start' and rerun."
    fi
    ok "podman machine is running"
    ;;
esac

# ---------------------------------------------------------------------------
# Pre-flight: RID detection
# ---------------------------------------------------------------------------
detect_rid() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"
  case "$os" in
    Linux)
      case "$arch" in
        x86_64|amd64)        echo "linux-x64" ;;
        aarch64|arm64)       echo "linux-arm64" ;;
        *) return 1 ;;
      esac ;;
    Darwin)
      case "$arch" in
        x86_64)              echo "osx-x64" ;;
        arm64|aarch64)       echo "osx-arm64" ;;
        *) return 1 ;;
      esac ;;
    MINGW*|MSYS*|CYGWIN*)
      echo "WIN_NOT_SUPPORTED"; return 0 ;;
    *) return 1 ;;
  esac
}

if ! RID="$(detect_rid)"; then
  fail "Unsupported OS/arch: $(uname -s)/$(uname -m). See https://github.com/${REPO_OWNER}/${REPO_NAME}/blob/main/docs/guide/operator/deployment.md for manual install."
fi
if [[ "$RID" == "WIN_NOT_SUPPORTED" ]]; then
  fail "Windows is not supported by install.sh in v0.1. Download spring-<v>-win-x64.zip from the GitHub release and follow docs/guide/operator/deployment.md for manual install."
fi
ok "RID detected: ${RID}"

# ---------------------------------------------------------------------------
# Pre-flight: already installed?
# ---------------------------------------------------------------------------
if [[ -e "${INSTALL_ROOT}/current" || -L "${INSTALL_ROOT}/current" ]]; then
  if [[ "$FORCE" -eq 0 ]]; then
    fail "Spring Voyage is already installed at ${INSTALL_ROOT}/current. Run \`voyage uninstall\` first, or rerun with --force to bypass (only do this if uninstall isn't working)."
  fi
  warn "${INSTALL_ROOT}/current exists; --force was passed, continuing."
fi

# ---------------------------------------------------------------------------
# Pre-flight: stale dispatcher process
# ---------------------------------------------------------------------------
# A prior install (or a crashed run) may have left ~/.spring-voyage/host/spring-dispatcher.pid
# pointing at a still-alive dispatcher. Re-running install on top of that
# silently shadows the existing process; the new install's deploy.sh up
# fails on the dispatcher port instead. Catch it explicitly here.
DISPATCHER_PID_FILE="${INSTALL_ROOT}/host/spring-dispatcher.pid"
if [[ -f "${DISPATCHER_PID_FILE}" ]]; then
  dispatcher_pid="$(tr -d '[:space:]' < "${DISPATCHER_PID_FILE}" 2>/dev/null || true)"
  if [[ -n "${dispatcher_pid}" ]] && kill -0 "${dispatcher_pid}" 2>/dev/null; then
    if [[ "$FORCE" -eq 0 ]]; then
      fail "Stale dispatcher process detected (PID ${dispatcher_pid} from a prior install). Run \`kill ${dispatcher_pid}\` or \`~/.spring-voyage/current/spring-voyage-host.sh stop\` before retrying."
    fi
    warn "Stale dispatcher PID ${dispatcher_pid} is alive; --force was passed, continuing."
  else
    info "Found stale dispatcher PID file at ${DISPATCHER_PID_FILE} (PID ${dispatcher_pid:-unknown} not alive); removing."
    rm -f "${DISPATCHER_PID_FILE}"
  fi
fi

# ---------------------------------------------------------------------------
# Pre-flight: ports
# ---------------------------------------------------------------------------
port_in_use() {
  local port="$1"
  # Try several detection paths; if none are available we skip silently.
  if command -v lsof >/dev/null 2>&1; then
    lsof -nP -iTCP:"${port}" -sTCP:LISTEN >/dev/null 2>&1
  elif command -v ss >/dev/null 2>&1; then
    ss -lnt 2>/dev/null | awk '{print $4}' | grep -Eq "[:.]${port}$"
  elif command -v netstat >/dev/null 2>&1; then
    netstat -an 2>/dev/null | awk '{print $4}' | grep -Eq "[:.]${port}$"
  else
    return 2
  fi
}

if [[ "${SPRING_INSTALL_SKIP_PORT_CHECK:-0}" == "1" ]]; then
  warn "SPRING_INSTALL_SKIP_PORT_CHECK=1 — skipping port-availability check (intended for tests / sandboxed runs)."
else
  # Dispatcher port matches the default in spring.env.example
  # (SPRING_DISPATCHER_PORT=8090). Operator overrides via env take effect.
  DISPATCHER_PORT="${SPRING_DISPATCHER_PORT:-8090}"
  PORT_CONFLICTS=()
  for port in 80 443 "${DISPATCHER_PORT}"; do
    if port_in_use "$port"; then
      PORT_CONFLICTS+=("$port")
    fi
  done
  if (( ${#PORT_CONFLICTS[@]} > 0 )); then
    fail "Port(s) already bound: ${PORT_CONFLICTS[*]}. Caddy needs 80 and 443 free, and the dispatcher needs ${DISPATCHER_PORT}. Stop the service holding the port (e.g. system nginx, a prior dispatcher) and rerun."
  fi
  ok "Ports 80, 443, ${DISPATCHER_PORT} free"
fi

# ---------------------------------------------------------------------------
# Pre-flight: ~/.local/bin on PATH
# ---------------------------------------------------------------------------
PATH_WARNING_NEEDED=0
case ":${PATH}:" in
  *":${BIN_DIR}:"*) ok "${BIN_DIR} is on PATH" ;;
  *)
    PATH_WARNING_NEEDED=1
    warn "${BIN_DIR} is NOT on your PATH."
    info "Add this line to your shell profile (e.g. ~/.bashrc or ~/.zshrc):"
    info "    export PATH=\"${BIN_DIR}:\$PATH\""
    info "Then reload the shell or run \`exec \$SHELL\`."
    ;;
esac

# ---------------------------------------------------------------------------
# Resolve release version
# ---------------------------------------------------------------------------
header "Resolving release version"

resolve_latest_release() {
  # Returns the resolved tag on stdout, or non-zero with an empty stdout.
  # The GitHub API returns JSON with a "tag_name" field.
  local body
  if ! body="$(curl -fsSL "${RELEASE_API_LATEST}" 2>/dev/null)"; then
    return 1
  fi
  # We don't depend on jq — extract `"tag_name": "vX.Y.Z"` with sed.
  local tag
  tag="$(printf '%s' "$body" | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n1)"
  [[ -n "$tag" ]] || return 1
  printf '%s' "$tag"
}

if [[ -z "$VERSION" ]]; then
  info "Resolving latest stable release from ${RELEASE_API_LATEST}..."
  if ! VERSION="$(resolve_latest_release)"; then
    fail "Could not resolve the latest release. Pass --version <tag> explicitly or set SPRING_VOYAGE_VERSION."
  fi
fi

# Normalise: strip leading 'v' for filename usage; keep the leading 'v' for URLs.
TAG="$VERSION"
[[ "$TAG" =~ ^v ]] || TAG="v${TAG}"
RELEASE_VERSION="${TAG#v}"
ok "Installing release: ${TAG} (version ${RELEASE_VERSION})"

# ---------------------------------------------------------------------------
# Layout
# ---------------------------------------------------------------------------
RELEASE_DIR="${INSTALL_ROOT}/releases/${RELEASE_VERSION}"
BUNDLE_DIR="${RELEASE_DIR}/bundle"
DISPATCHER_DIR="${RELEASE_DIR}/dispatcher"
CLI_DIR="${RELEASE_DIR}/cli"
DOWNLOAD_DIR="${RELEASE_DIR}/.downloads"

mkdir -p "${RELEASE_DIR}" "${DOWNLOAD_DIR}"

# ---------------------------------------------------------------------------
# Download assets + SHA256SUMS
# ---------------------------------------------------------------------------
header "Downloading release assets"

download_asset() {
  local name="$1"
  local url="${RELEASE_DOWNLOAD_PREFIX}/${TAG}/${name}"
  local dest="${DOWNLOAD_DIR}/${name}"
  info "Downloading ${name}..."
  if ! curl -fSL --retry 3 --retry-delay 2 -o "${dest}" "${url}"; then
    fail "Download failed: ${url}"
  fi
}

BUNDLE_ASSET="spring-voyage-${RELEASE_VERSION}-bundle.tar.gz"
DISPATCHER_ASSET="spring-voyage-dispatcher-${RELEASE_VERSION}-${RID}.tar.gz"
CLI_ASSET="spring-${RELEASE_VERSION}-${RID}.tar.gz"

download_asset "SHA256SUMS"
download_asset "${BUNDLE_ASSET}"
download_asset "${DISPATCHER_ASSET}"
download_asset "${CLI_ASSET}"
ok "All assets downloaded"

# ---------------------------------------------------------------------------
# Verify checksums
# ---------------------------------------------------------------------------
header "Verifying SHA256 checksums"

verify_one() {
  local file="$1"
  local sums="${DOWNLOAD_DIR}/SHA256SUMS"
  local expected actual
  expected="$(awk -v f="${file}" '$2==f || $2=="*"f {print $1; exit}' "${sums}")"
  if [[ -z "$expected" ]]; then
    fail "SHA256SUMS does not contain an entry for ${file}."
  fi
  actual="$(${SHA256_TOOL} "${DOWNLOAD_DIR}/${file}" | awk '{print $1}')"
  if [[ "$expected" != "$actual" ]]; then
    fail "Checksum mismatch for ${file}: expected ${expected}, got ${actual}."
  fi
  ok "${file} checksum OK"
}

verify_one "${BUNDLE_ASSET}"
verify_one "${DISPATCHER_ASSET}"
verify_one "${CLI_ASSET}"

# ---------------------------------------------------------------------------
# Extract
# ---------------------------------------------------------------------------
header "Extracting assets"

# The bundle tarball is structured as `bundle/...`. Extract into RELEASE_DIR
# so we get RELEASE_DIR/bundle/...
rm -rf "${BUNDLE_DIR}" "${DISPATCHER_DIR}" "${CLI_DIR}"
tar -xzf "${DOWNLOAD_DIR}/${BUNDLE_ASSET}" -C "${RELEASE_DIR}"
[[ -d "${BUNDLE_DIR}" ]] || fail "Bundle tarball did not produce expected layout at ${BUNDLE_DIR}."
ok "Bundle extracted -> ${BUNDLE_DIR}"

mkdir -p "${DISPATCHER_DIR}"
tar -xzf "${DOWNLOAD_DIR}/${DISPATCHER_ASSET}" -C "${DISPATCHER_DIR}"
ok "Dispatcher extracted -> ${DISPATCHER_DIR}"

mkdir -p "${CLI_DIR}"
tar -xzf "${DOWNLOAD_DIR}/${CLI_ASSET}" -C "${CLI_DIR}"
ok "CLI extracted -> ${CLI_DIR}"

# ---------------------------------------------------------------------------
# Read manifest
# ---------------------------------------------------------------------------
MANIFEST="${BUNDLE_DIR}/manifest.json"
[[ -f "$MANIFEST" ]] || fail "Bundle is missing manifest.json (${MANIFEST})."

PLATFORM_IMAGE="$(sed -n 's/.*"platform_image"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "${MANIFEST}" | head -n1)"
[[ -n "$PLATFORM_IMAGE" ]] || fail "Could not read platform_image from ${MANIFEST}."
ok "Platform image (from manifest): ${PLATFORM_IMAGE}"

# ---------------------------------------------------------------------------
# Locate binaries (handles both flat archives and stage-dir archives)
# ---------------------------------------------------------------------------
locate_binary() {
  local dir="$1" name="$2"
  if [[ -x "${dir}/${name}" ]]; then
    printf '%s' "${dir}/${name}"; return 0
  fi
  # Some archives may extract into a nested dir.
  local found
  found="$(find "${dir}" -maxdepth 3 -type f -name "${name}" 2>/dev/null | head -n1)"
  if [[ -n "$found" ]]; then
    chmod +x "$found" 2>/dev/null || true
    printf '%s' "$found"; return 0
  fi
  return 1
}

if ! DISPATCHER_BIN="$(locate_binary "${DISPATCHER_DIR}" "Cvoya.Spring.Dispatcher")"; then
  fail "Could not locate dispatcher binary in ${DISPATCHER_DIR}."
fi
ok "Dispatcher binary: ${DISPATCHER_BIN}"

if ! CLI_BIN="$(locate_binary "${CLI_DIR}" "spring")"; then
  fail "Could not locate spring CLI binary in ${CLI_DIR}."
fi
chmod +x "${CLI_BIN}"
ok "CLI binary: ${CLI_BIN}"

# ---------------------------------------------------------------------------
# Pull the platform image
# ---------------------------------------------------------------------------
header "Pulling platform image"
info "podman pull ${PLATFORM_IMAGE}"
if ! podman pull "${PLATFORM_IMAGE}"; then
  fail "podman pull ${PLATFORM_IMAGE} failed. Check network connectivity and try again."
fi
ok "Platform image pulled"

# ---------------------------------------------------------------------------
# Prompt: DEPLOY_HOSTNAME
# ---------------------------------------------------------------------------
header "Configuring deployment"

DEPLOY_HOSTNAME_DEFAULT="localhost"
DEPLOY_HOSTNAME="$DEPLOY_HOSTNAME_DEFAULT"
if [[ "$ASSUME_YES" -eq 0 ]]; then
  printf '  %sDEPLOY_HOSTNAME%s [%s]: ' "${BOLD}" "${NC}" "$DEPLOY_HOSTNAME_DEFAULT" >&2
  if [[ -t 0 ]]; then
    IFS= read -r answer || answer=""
  else
    # When piped from `curl ... | bash`, stdin is the curl stream — read
    # from the tty so we can still prompt the user.
    if [[ -r /dev/tty ]]; then
      IFS= read -r answer </dev/tty || answer=""
    else
      answer=""
    fi
  fi
  if [[ -n "${answer:-}" ]]; then
    DEPLOY_HOSTNAME="$answer"
  fi
fi
ok "DEPLOY_HOSTNAME=${DEPLOY_HOSTNAME}"

# ---------------------------------------------------------------------------
# Symlinks
# ---------------------------------------------------------------------------
mkdir -p "${BIN_DIR}"
ln -snf "${BUNDLE_DIR}" "${INSTALL_ROOT}/current"
ln -snf "${CLI_BIN}"    "${BIN_DIR}/spring"
ok "Symlinked ${INSTALL_ROOT}/current -> ${BUNDLE_DIR}"
ok "Symlinked ${BIN_DIR}/spring -> ${CLI_BIN}"

# voyage wrapper: status / logs / restart / install / uninstall / version.
# The wrapper script ships in the bundle as bundle/voyage (see
# .github/workflows/release.yml). We copy it (not symlink) so a later
# uninstall that removes the install root leaves the wrapper in place
# long enough to be removed by uninstall.sh itself. install.sh runs
# from a curl pipe with no checked-out tree, so the bundle is the only
# checked-in source available at install time.
WRAPPER_PATH="${BIN_DIR}/voyage"
WRAPPER_SRC="${BUNDLE_DIR}/voyage"
if [[ ! -f "${WRAPPER_SRC}" ]]; then
  fail "Bundle is missing voyage wrapper at ${WRAPPER_SRC}."
fi
cp "${WRAPPER_SRC}" "${WRAPPER_PATH}"
chmod +x "${WRAPPER_PATH}"
ok "Wrote ${WRAPPER_PATH}"

# Bundle's uninstall.sh may not be marked executable inside the tarball.
if [[ -f "${BUNDLE_DIR}/uninstall.sh" ]]; then
  chmod +x "${BUNDLE_DIR}/uninstall.sh"
fi
if [[ -f "${BUNDLE_DIR}/deploy.sh" ]]; then
  chmod +x "${BUNDLE_DIR}/deploy.sh"
fi
if [[ -f "${BUNDLE_DIR}/spring-voyage-host.sh" ]]; then
  chmod +x "${BUNDLE_DIR}/spring-voyage-host.sh"
fi

# ---------------------------------------------------------------------------
# Generate spring.env
# ---------------------------------------------------------------------------
header "Generating spring.env"

ENV_EXAMPLE="${BUNDLE_DIR}/spring.env.example"
SPRING_ENV_FILE="${INSTALL_ROOT}/spring.env"
[[ -f "$ENV_EXAMPLE" ]] || fail "Bundle is missing spring.env.example at ${ENV_EXAMPLE}."

# Generate secrets the same way devops/deploy/setup.sh does, just without
# prompting. We mirror the entropy and encoding choices so an operator can
# audit one path and trust the other.
POSTGRES_PASSWORD="$(openssl rand -hex 8 | head -c 16)"
SPRING_SECRETS_AES_KEY="$(openssl rand -base64 32 | tr -d '\n')"

REDIRECT_URI="https://${DEPLOY_HOSTNAME}/api/v1/tenant/connectors/github/oauth/callback"
DAPR_COMPONENTS_PATH="${INSTALL_ROOT}/current/dapr/components/delegated-spring-voyage-agent"

# Copy template then append the installer-managed section. We do NOT edit
# the template values in place; appended values shadow earlier ones when
# read with `set -a; source spring.env` (last-write-wins) and the appended
# section is the documented installer surface for re-runs.
cp "${ENV_EXAMPLE}" "${SPRING_ENV_FILE}"
chmod 600 "${SPRING_ENV_FILE}"

{
  printf '\n'
  printf '# ---------------------------------------------------------------------------\n'
  printf '# Written by devops/install/install.sh on %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
  printf '# Edit below to reconfigure; values here shadow the spring.env.example defaults.\n'
  printf '# ---------------------------------------------------------------------------\n'
  printf 'SPRING_IMAGE_TAG=%s\n' "${RELEASE_VERSION}"
  printf 'SPRING_PLATFORM_IMAGE=%s\n' "${PLATFORM_IMAGE}"
  printf 'POSTGRES_PASSWORD=%s\n' "${POSTGRES_PASSWORD}"
  printf 'SPRING_SECRETS_AES_KEY=%s\n' "${SPRING_SECRETS_AES_KEY}"
  printf 'DEPLOY_HOSTNAME=%s\n' "${DEPLOY_HOSTNAME}"
  printf 'GitHub__OAuth__RedirectUri=%s\n' "${REDIRECT_URI}"
  printf 'Dapr__Sidecar__DelegatedSpringVoyageAgentComponentsPath=%s\n' "${DAPR_COMPONENTS_PATH}"
  printf 'SPRING_DISPATCHER_BIN=%s\n' "${DISPATCHER_BIN}"
} >> "${SPRING_ENV_FILE}"

chmod 600 "${SPRING_ENV_FILE}"
ok "Wrote ${SPRING_ENV_FILE} (mode 0600)"

# ---------------------------------------------------------------------------
# Start the stack (unless --no-start)
# ---------------------------------------------------------------------------
DEPLOY_SH="${BUNDLE_DIR}/deploy.sh"
[[ -x "$DEPLOY_SH" ]] || fail "deploy.sh not found in bundle at ${DEPLOY_SH}."

STACK_STARTED=0
if [[ "$NO_START" -eq 1 ]]; then
  header "Skipping stack start (--no-start)"
  info "Run: SPRING_ENV_FILE=${SPRING_ENV_FILE} ${DEPLOY_SH} up"
else
  header "Starting the stack"
  info "SPRING_ENV_FILE=${SPRING_ENV_FILE} ${DEPLOY_SH} up"
  if SPRING_ENV_FILE="${SPRING_ENV_FILE}" "${DEPLOY_SH}" up; then
    STACK_STARTED=1
    ok "Stack is up"
  else
    warn "deploy.sh up exited non-zero. Inspect logs and rerun:"
    info "  SPRING_ENV_FILE=${SPRING_ENV_FILE} ${DEPLOY_SH} up"
  fi
fi

# ---------------------------------------------------------------------------
# Optional: GitHub App manifest flow
# ---------------------------------------------------------------------------
GITHUB_APP_PROMPTED=0
GITHUB_APP_CONFIGURED=0
if [[ "$STACK_STARTED" -eq 1 && "$ASSUME_YES" -eq 0 ]]; then
  header "GitHub App registration"
  info "Spring Voyage's GitHub connector needs a per-deployment GitHub App."
  info "The CLI can drive the manifest flow now (single browser click on GitHub)."
  info "You can also skip this and run \`spring github-app register\` later."
  printf '  %sConfigure GitHub App for this deployment?%s [Y/n]: ' "${BOLD}" "${NC}" >&2
  if [[ -t 0 ]]; then
    IFS= read -r answer || answer=""
  elif [[ -r /dev/tty ]]; then
    IFS= read -r answer </dev/tty || answer=""
  else
    answer=""
  fi
  GITHUB_APP_PROMPTED=1
  case "${answer:-Y}" in
    [Nn]|[Nn][Oo])
      info "Skipped. To enable GitHub features later, run: spring github-app register --env-path ${SPRING_ENV_FILE}"
      ;;
    *)
      # Default app name suggestion; operator can change inside the GitHub
      # creation UI. CLI requires --name to be set.
      DEFAULT_APP_NAME="spring-voyage-${DEPLOY_HOSTNAME//[^A-Za-z0-9]/-}"
      info "Running: spring github-app register --name ${DEFAULT_APP_NAME} --env-path ${SPRING_ENV_FILE} --write-env"
      if "${BIN_DIR}/spring" github-app register \
            --name "${DEFAULT_APP_NAME}" \
            --env-path "${SPRING_ENV_FILE}" \
            --write-env; then
        GITHUB_APP_CONFIGURED=1
        ok "GitHub App registered; credentials written to ${SPRING_ENV_FILE}"
        info "Restarting the stack to pick up the new credentials..."
        if SPRING_ENV_FILE="${SPRING_ENV_FILE}" "${DEPLOY_SH}" restart; then
          ok "Stack restarted"
        else
          warn "deploy.sh restart returned non-zero; run it manually after inspecting logs."
        fi
      else
        warn "spring github-app register failed or timed out."
        info "Re-run later: spring github-app register --env-path ${SPRING_ENV_FILE} --write-env"
      fi
      ;;
  esac
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
header "Install complete"

WEB_URL="https://${DEPLOY_HOSTNAME}"
[[ "$DEPLOY_HOSTNAME" == "localhost" ]] && WEB_URL="http://localhost"

cat <<EOF

  Install root:        ${INSTALL_ROOT}
  spring.env:          ${SPRING_ENV_FILE}
  Current symlink:     ${INSTALL_ROOT}/current -> ${BUNDLE_DIR}
  CLI:                 ${BIN_DIR}/spring
  Wrapper:             ${BIN_DIR}/voyage
  Logs (containers):   podman logs spring-api (and friends)
  Logs (dispatcher):   ${INSTALL_ROOT}/host/spring-dispatcher.log
  Web URL:             ${WEB_URL}

  Day-2 commands:
    voyage status               # install version, container/dispatcher health, web URL
    voyage logs [service]       # tail container logs (or 'dispatcher' for the host process)
    voyage restart              # restart the stack
    voyage version              # print the installed version + image tag

  To tear down later:
    voyage uninstall            # preserves spring.env + workspaces
    voyage uninstall --purge    # factory reset

EOF

if [[ "$PATH_WARNING_NEEDED" -eq 1 ]]; then
  warn "Don't forget to add ${BIN_DIR} to your PATH (see the export line above)."
fi

if [[ "$GITHUB_APP_PROMPTED" -eq 0 || "$GITHUB_APP_CONFIGURED" -eq 0 ]]; then
  cat <<EOF
  Next: enable GitHub features
    spring github-app register --env-path ${SPRING_ENV_FILE} --write-env
    # Then: SPRING_ENV_FILE=${SPRING_ENV_FILE} ${DEPLOY_SH} restart

EOF
fi

cat <<EOF
  Next: configure LLM provider credentials
    spring secret create --scope tenant anthropic-api-key --value 'sk-ant-...'
    spring secret create --scope tenant openai-api-key    --value 'sk-...'

  Full operator docs: https://github.com/${REPO_OWNER}/${REPO_NAME}/blob/main/docs/guide/operator/deployment.md

EOF
