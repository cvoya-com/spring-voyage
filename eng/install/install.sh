#!/usr/bin/env bash
# Spring Voyage — source-free local-host installer.
#
# Canonical operator entry-point (ADR-0042). Downloads the platform image
# and the per-RID host archive (containing the deployment bundle, the
# dispatcher binary, and the `spring` CLI in one tarball — #2243) from a
# GitHub Release, verifies it against the release's SHA256SUMS, generates
# a spring.env with reasonable defaults, and brings the stack up via
# `deploy.sh up`. Local-host only — no SSH, no remote container contexts.
#
# Usage:
#   curl -fSL https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh | bash
#
#   install.sh [--version <tag>] [--root <dir>] [--yes] [--force] [--no-start]
#
# Two prompts in the common path:
#   1. DEPLOY_HOSTNAME (default `localhost`)
#   2. Configure GitHub App for this deployment? (Y/n)
#
# Plus a conditional prompt: if a default host port (80/443/8090/5050) is
# already in use, the installer offers to remap it and records the choice in
# spring.env. `--yes` skips the two prompts and, on a port conflict, fails
# with guidance instead of prompting. Passwords, AES keys, and paths are
# auto-generated or auto-derived from the bundle's manifest.json. See
# ADR-0042 for the full decision record.

set -euo pipefail

# ---------------------------------------------------------------------------
# Constants & defaults
# ---------------------------------------------------------------------------
REPO_OWNER="cvoya-com"
REPO_NAME="spring-voyage"
RELEASE_API_LATEST="https://api.github.com/repos/${REPO_OWNER}/${REPO_NAME}/releases/latest"
RELEASE_DOWNLOAD_PREFIX="https://github.com/${REPO_OWNER}/${REPO_NAME}/releases/download"

# When this installer is baked for a specific release (the `install-<v>.sh`
# variant attached to a GitHub Release), the `BAKED_VERSION` assignment
# below is filled in at release-publish time by the `publish-installer`
# workflow job. The unversioned `install.sh` keeps the empty assignment
# and resolves the version dynamically from --version, SPRING_VOYAGE_VERSION,
# or the GitHub API. The versioned `install-<v>.sh` refuses to install any
# other version.
BAKED_VERSION=""

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

# Build a "host[:port]" authority, omitting the port when it is the scheme
# default (so the common 80/443 install keeps clean http://host URLs).
http_authority() {
  local host="$1" port="$2" default="$3"
  if [[ "$port" == "$default" ]]; then
    printf '%s' "$host"
  else
    printf '%s:%s' "$host" "$port"
  fi
}

# Best-effort check that DEPLOY_HOSTNAME is ready for the Let's Encrypt challenge:
# its DNS resolves to an IP configured on this host, and Caddy keeps the standard
# 80/443 ports. External reachability can't be verified from the host itself, so
# this errs toward "not ready" — a false negative keeps the safe HTTP default; a
# false positive would leave the site unserved on a failed ACME challenge (#2928).
acme_ready() {
  local host="$1" resolved local_ips rip
  # Remapped Caddy ports can't satisfy the HTTP-01 / TLS-ALPN challenge at all.
  [[ "${CADDY_HTTP_PORT:-80}" == "80" && "${CADDY_HTTPS_PORT:-443}" == "443" ]] || return 1
  resolved="$(getent ahosts "$host" 2>/dev/null | awk '{print $1}' | sort -u)"
  [[ -n "$resolved" ]] || return 1
  local_ips="$( { hostname -I 2>/dev/null; ip -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1; } | tr ' ' '\n' | sort -u )"
  [[ -n "$local_ips" ]] || return 1
  while IFS= read -r rip; do
    [[ -n "$rip" ]] && printf '%s\n' "$local_ips" | grep -qxF "$rip" && return 0
  done <<< "$resolved"
  return 1
}

# ---------------------------------------------------------------------------
# Usage
# ---------------------------------------------------------------------------
usage() {
  cat <<'EOF'
Spring Voyage — source-free local-host installer.

Usage:
  install.sh [--version <tag>] [--root <dir>] [--yes] [--force] [--no-start]

Options:
  --version <tag>   Release version to install. Accepted forms: '1.0.0',
                    'v1.0.0', or 'spring-voyage-v1.0.0'. Defaults to
                    $SPRING_VOYAGE_VERSION or the latest stable release.
  --root <dir>      Install root (default: ~/.spring-voyage).
  --yes             Non-interactive. Uses DEPLOY_HOSTNAME=localhost, skips
                    the GitHub App manifest flow, and fails (rather than
                    prompting) if a required host port is already in use.
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
# envsubst (gettext) is required by the bundle's deploy.sh (it expands ${VAR}
# references in spring.env before passing it to podman). Checking it here avoids
# a late failure at `deploy.sh up`, after the archive download and image pull.
require_tool envsubst "Install gettext (Debian/Ubuntu: apt-get install gettext-base; macOS: brew install gettext) and rerun."

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
  fail "Windows is not supported by install.sh in v0.1. Download spring-voyage-<v>-win-x64.zip from the GitHub release and follow docs/guide/operator/deployment.md for manual install."
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
# Pre-flight: host-published ports
# ---------------------------------------------------------------------------
# Spring Voyage publishes exactly four ports on the host. Everything else
# (Postgres, the Dapr sidecars, the internal :8443 tenant listener, the
# container-internal :8080/:3000) stays on the container networks and never
# touches the host, so we don't check those.
#
#   80    Caddy HTTP         override CADDY_HTTP_PORT
#   443   Caddy HTTPS        override CADDY_HTTPS_PORT
#   8090  dispatcher         override SPRING_DISPATCHER_PORT
#   5050  worker MCP server  override Mcp__Port
#
# Each override is sourced from spring.env by deploy.sh and
# spring-voyage-host.sh, so any remap chosen here is persisted to the
# installer-managed section of spring.env below and applied on every
# `deploy.sh up`.

# 0 = a process is listening, 1 = free, 2 = could not determine (no tool).
# Prefer ss: it lists every listener from the kernel table regardless of owner,
# so a non-root run still sees root-owned listeners (e.g. a system reverse proxy
# on 80/443). lsof as a non-root user cannot see other users' sockets and would
# report such a port free.
port_in_use() {
  local port="$1"
  if command -v ss >/dev/null 2>&1; then
    ss -lnt 2>/dev/null | awk '{print $4}' | grep -Eq "[:.]${port}$" && return 0 || return 1
  elif command -v lsof >/dev/null 2>&1; then
    lsof -nP -iTCP:"${port}" -sTCP:LISTEN >/dev/null 2>&1 && return 0 || return 1
  elif command -v netstat >/dev/null 2>&1; then
    netstat -an 2>/dev/null | awk '{print $4}' | grep -Eq "[:.]${port}$" && return 0 || return 1
  fi
  return 2
}

# Scan upward from $1 for a port nothing is listening on (best-effort; an
# "unknown" result counts as free since we cannot prove otherwise). The scan is
# clamped to min_bindable_port, so on a rootless host it never returns a port
# below the kernel's unprivileged floor (free but unbindable).
next_free_port() {
  local p="$1" i status floor
  floor="$(min_bindable_port)"
  (( p < floor - 1 )) && p=$(( floor - 1 ))
  for (( i = 0; i < 200; i++ )); do
    p=$(( p + 1 ))
    (( p > 65535 )) && p="$floor"
    status=0; port_in_use "$p" || status=$?
    (( status != 0 )) && { printf '%s' "$p"; return 0; }
  done
  printf '%s' "$1"
}

# Pick a free host port at or above $1 (falls back to next_free_port on conflict).
free_port_at_or_above() {
  local p="$1" status=0
  port_in_use "$p" || status=$?
  if (( status == 0 )); then next_free_port "$p"; else printf '%s' "$p"; fi
}

# Lowest host port a rootless publish can actually bind: on Linux the kernel's
# unprivileged-port floor, elsewhere (macOS podman-machine) 1 — no such limit.
# Keeps port suggestions bindable.
min_bindable_port() {
  if [[ "$(uname -s)" == "Linux" ]]; then
    local f; f="$(unprivileged_port_start)"
    [[ "$f" =~ ^[0-9]+$ ]] && { printf '%s' "$f"; return 0; }
  fi
  printf '1'
}

# Suggest a free, bindable port when `candidate` (configured default `default`)
# is taken. For a privileged default on a rootless host, jump to the conventional
# high alternative (80->8080, 443->8443) — nearby low ports are also unbindable;
# otherwise scan up from the candidate. Always >= the floor.
suggest_port() {
  local candidate="$1" default="$2"
  local floor; floor="$(min_bindable_port)"
  local base=$(( candidate + 1 ))
  if (( default < floor )); then
    base=$(( default + 8000 ))
    (( base < floor )) && base="$floor"
  fi
  free_port_at_or_above "$base"
}

# ---------------------------------------------------------------------------
# Rootless privileged-port handling (Linux only)
# ---------------------------------------------------------------------------
# A *free* port is not necessarily a *bindable* one. Under rootless Podman the
# kernel blocks unprivileged binds below net.ipv4.ip_unprivileged_port_start
# (default 1024), so Caddy's 80/443 host mapping fails at `deploy.sh up` with
# "rootlessport cannot expose privileged port" even though the availability
# check above passed. We detect that here, before the download, and either
# lower the threshold (keeps 80/443 + automatic Let's Encrypt) or remap to
# high ports. macOS is exempt — podman machine forwards host ports through its
# VM and is not subject to this floor.

# Echo the kernel's unprivileged-port floor. Honors SPRING_INSTALL_UNPRIV_PORT_START
# (tests / advanced operators), then /proc, then sysctl, then the kernel default.
unprivileged_port_start() {
  if [[ -n "${SPRING_INSTALL_UNPRIV_PORT_START:-}" ]]; then
    printf '%s' "${SPRING_INSTALL_UNPRIV_PORT_START}"; return 0
  fi
  local v
  if [[ -r /proc/sys/net/ipv4/ip_unprivileged_port_start ]]; then
    v="$(tr -d '[:space:]' < /proc/sys/net/ipv4/ip_unprivileged_port_start 2>/dev/null || true)"
    [[ -n "$v" ]] && { printf '%s' "$v"; return 0; }
  fi
  if command -v sysctl >/dev/null 2>&1; then
    v="$(sysctl -n net.ipv4.ip_unprivileged_port_start 2>/dev/null || true)"
    [[ -n "$v" ]] && { printf '%s' "$v"; return 0; }
  fi
  printf '1024'
}

# Lower the floor to $1 (persisted under /etc/sysctl.d) via sudo. Returns
# non-zero if sudo is unavailable or the operator declines / it fails.
lower_unprivileged_port_start() {
  local target="$1" conf="/etc/sysctl.d/99-spring-voyage.conf"
  command -v sudo >/dev/null 2>&1 || return 1
  info "Lowering net.ipv4.ip_unprivileged_port_start to ${target} (writes ${conf}; sudo may prompt)..."
  sudo sh -c "printf 'net.ipv4.ip_unprivileged_port_start=%s\n' '${target}' > '${conf}'" || return 1
  sudo sysctl --system >/dev/null 2>&1 || return 1
  return 0
}

# Ensure the resolved Caddy host ports are bindable under rootless Podman.
# Updates CADDY_HTTP_PORT / CADDY_HTTPS_PORT in place when it remaps.
resolve_privileged_ports() {
  [[ "$(uname -s)" == "Linux" ]] || return 0

  local floor; floor="$(unprivileged_port_start)"
  [[ "$floor" =~ ^[0-9]+$ ]] || floor=1024

  local http="${CADDY_HTTP_PORT:-80}" https="${CADDY_HTTPS_PORT:-443}"
  local below=""
  if (( http  < floor )); then below+="HTTP ${http} "; fi
  if (( https < floor )); then below+="HTTPS ${https} "; fi
  if [[ -z "$below" ]]; then
    ok "Caddy host ports bindable under rootless Podman (unprivileged floor = ${floor})"
    return 0
  fi
  below="${below% }"

  local target conf="/etc/sysctl.d/99-spring-voyage.conf"
  target=$(( http < https ? http : https ))

  warn "Rootless Podman cannot bind ${below} — the kernel allows unprivileged binds only from port ${floor} up (net.ipv4.ip_unprivileged_port_start)."

  # Non-interactive: fail fast with both remedies (mirrors the port-conflict path).
  if [[ "$ASSUME_YES" -eq 1 ]] || { [[ ! -t 0 ]] && [[ ! -r /dev/tty ]]; }; then
    fail "Caddy's web ports are not bindable under rootless Podman. Pick one and rerun:
       A) Lower the threshold (keeps ${http}/${https} + automatic TLS):
            echo 'net.ipv4.ip_unprivileged_port_start=${target}' | sudo tee ${conf}
            sudo sysctl --system
       B) Use high host ports (no sudo; portal on :8080, automatic Let's Encrypt disabled):
            CADDY_HTTP_PORT=8080 CADDY_HTTPS_PORT=8443 <install command>
            (or add those two lines to ${INSTALL_ROOT}/spring.env)"
  fi

  # Interactive: offer both, recommend lowering the threshold.
  info "How would you like to proceed?"
  info "  1) Lower the threshold to ${target} — recommended; keeps ${http}/${https} + automatic TLS (needs sudo)"
  info "  2) Use high ports 8080/8443 — no sudo; portal on :8080, automatic Let's Encrypt disabled"
  local answer choice=""
  while [[ -z "$choice" ]]; do
    printf '  %sOption%s [1]: ' "${BOLD}" "${NC}" >&2
    if [[ -t 0 ]]; then
      IFS= read -r answer || answer=""
    elif [[ -r /dev/tty ]]; then
      IFS= read -r answer </dev/tty || answer=""
    else
      answer=""
    fi
    [[ -z "$answer" ]] && answer="1"
    case "$answer" in
      1|2) choice="$answer" ;;
      *)   warn "Enter 1 or 2." ;;
    esac
  done

  if [[ "$choice" == "1" ]]; then
    if lower_unprivileged_port_start "$target"; then
      floor="$(unprivileged_port_start)"; [[ "$floor" =~ ^[0-9]+$ ]] || floor=1024
      if (( http >= floor && https >= floor )); then
        ok "Lowered net.ipv4.ip_unprivileged_port_start to ${floor}; ${http}/${https} are now bindable."
        return 0
      fi
      warn "Applied, but the floor is still ${floor}; falling back to high ports."
    else
      warn "Could not lower the threshold automatically (sudo unavailable or declined)."
      info "Run it later if you want ${http}/${https} back:"
      info "    echo 'net.ipv4.ip_unprivileged_port_start=${target}' | sudo tee ${conf} && sudo sysctl --system"
      info "Falling back to high ports."
    fi
  fi

  # Remap to high ports (option 2, or option 1 that could not lower the floor).
  CADDY_HTTP_PORT="$(free_port_at_or_above 8080)"
  CADDY_HTTPS_PORT="$(free_port_at_or_above 8443)"
  warn "Using high host ports — HTTP ${CADDY_HTTP_PORT}, HTTPS ${CADDY_HTTPS_PORT}. Automatic Let's Encrypt is disabled off 80/443; terminate TLS upstream or front Caddy with a reverse proxy."
  ok "Caddy host ports -> ${CADDY_HTTP_PORT}/${CADDY_HTTPS_PORT}"
  return 0
}

PORT_CHECK_UNAVAILABLE=0

# Resolve one host port. Honors an existing env override as the candidate,
# prompts for an alternative when the candidate is taken and a TTY is
# available, or fails with actionable guidance when non-interactive. Stores
# the resolved value back into the named variable.
resolve_host_port() {
  local label="$1" varname="$2" default="$3"
  local candidate="${!varname:-$default}"
  local status=0
  port_in_use "$candidate" || status=$?

  if (( status == 2 )); then
    PORT_CHECK_UNAVAILABLE=1
    printf -v "$varname" '%s' "$candidate"
    return 0
  fi
  if (( status == 1 )); then
    printf -v "$varname" '%s' "$candidate"
    return 0
  fi

  # Candidate is in use — offer a free, bindable alternative.
  local suggestion answer
  suggestion="$(suggest_port "$candidate" "$default")"
  if [[ "$ASSUME_YES" -eq 1 ]] || { [[ ! -t 0 ]] && [[ ! -r /dev/tty ]]; }; then
    fail "Port ${candidate} (${label}) is already in use. Set ${varname} to an open port and rerun — e.g. \`${varname}=${suggestion} install.sh ...\` (or add \`${varname}=${suggestion}\` to ${INSTALL_ROOT}/spring.env)."
  fi

  warn "Port ${candidate} (${label}) is already in use."
  while :; do
    printf '  %sNew %s port%s [%s]: ' "${BOLD}" "${label}" "${NC}" "${suggestion}" >&2
    if [[ -t 0 ]]; then
      IFS= read -r answer || answer=""
    elif [[ -r /dev/tty ]]; then
      IFS= read -r answer </dev/tty || answer=""
    else
      answer=""
    fi
    [[ -z "$answer" ]] && answer="$suggestion"
    # ^[1-9][0-9]*$ rules out leading zeros (so no octal parse) and 0 itself,
    # leaving only the upper bound to check.
    if ! [[ "$answer" =~ ^[1-9][0-9]*$ ]] || (( answer > 65535 )); then
      warn "Enter a port number between 1 and 65535."
      continue
    fi
    status=0; port_in_use "$answer" || status=$?
    if (( status == 0 )); then
      warn "Port ${answer} is also in use; choose another."
      suggestion="$(next_free_port "$answer")"
      continue
    fi
    printf -v "$varname" '%s' "$answer"
    ok "${label} -> port ${answer}"
    return 0
  done
}

if [[ "${SPRING_INSTALL_SKIP_PORT_CHECK:-0}" == "1" ]]; then
  warn "SPRING_INSTALL_SKIP_PORT_CHECK=1 — skipping port-availability check (intended for tests / sandboxed runs)."
else
  resolve_host_port "Caddy HTTP"        CADDY_HTTP_PORT        80
  resolve_host_port "Caddy HTTPS"       CADDY_HTTPS_PORT       443
  resolve_host_port "dispatcher"        SPRING_DISPATCHER_PORT 8090
  resolve_host_port "worker MCP server" Mcp__Port              5050
  if (( PORT_CHECK_UNAVAILABLE == 1 )); then
    warn "Could not verify port availability (no lsof/ss/netstat found). Proceeding anyway; if 'deploy.sh up' later fails with 'address already in use', set CADDY_HTTP_PORT / CADDY_HTTPS_PORT / SPRING_DISPATCHER_PORT / Mcp__Port in ${INSTALL_ROOT}/spring.env and rerun."
  else
    ok "Host ports free — HTTP ${CADDY_HTTP_PORT:-80}, HTTPS ${CADDY_HTTPS_PORT:-443}, dispatcher ${SPRING_DISPATCHER_PORT:-8090}, MCP ${Mcp__Port:-5050}"
  fi
  # A free port is not necessarily bindable: rootless Podman cannot publish
  # privileged ports (80/443) below the kernel's unprivileged floor. Resolve
  # that now (Linux only) so we don't fail at the very end of `deploy.sh up`.
  resolve_privileged_ports
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
# Accepted input forms (precedence: --version > $SPRING_VOYAGE_VERSION >
# BAKED_VERSION > latest stable from GitHub API):
#
#   '1.0.0'                  → SEMVER=1.0.0
#   'v1.0.0'                 → SEMVER=1.0.0
#   'spring-voyage-v1.0.0'   → SEMVER=1.0.0
#
# Resolved values:
#   TAG    = spring-voyage-v${SEMVER}   (the actual release tag, post-#2229)
#   SEMVER = 1.0.0                       (used in asset filenames)
#
# Bug fix (#2243): before this rewrite, the script normalised by prepending
# a single `v` to a non-`v` tag, but the GitHub API now returns
# `spring-voyage-v1.0.0` (#2229 renamed the tag scheme). The old code
# produced `vspring-voyage-v1.0.0` and the download URL 404'd. install.sh
# has been broken against any real release since #2229 merged.
header "Resolving release version"

resolve_latest_release() {
  # Returns the resolved tag on stdout, or non-zero with an empty stdout.
  # The GitHub API returns JSON with a "tag_name" field.
  local body
  if ! body="$(curl -fsSL "${RELEASE_API_LATEST}" 2>/dev/null)"; then
    return 1
  fi
  # We don't depend on jq — extract `"tag_name": "spring-voyage-vX.Y.Z"`
  # with sed.
  local tag
  tag="$(printf '%s' "$body" | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n1)"
  [[ -n "$tag" ]] || return 1
  printf '%s' "$tag"
}

# If this is the version-baked install-<v>.sh variant, the operator-passed
# VERSION (if any) must match the baked one — refuse otherwise so the
# stable URL semantics ("install-<v>.sh installs exactly version <v>") are
# preserved.
if [[ -n "$BAKED_VERSION" ]]; then
  if [[ -n "$VERSION" ]]; then
    _user_semver="${VERSION#spring-voyage-}"
    _user_semver="${_user_semver#v}"
    _baked_semver="${BAKED_VERSION#spring-voyage-}"
    _baked_semver="${_baked_semver#v}"
    if [[ "$_user_semver" != "$_baked_semver" ]]; then
      fail "This installer is baked for version ${BAKED_VERSION}. To install a different version, fetch the unversioned install.sh from releases/latest/download/install.sh, or use install-<other-version>.sh."
    fi
  fi
  VERSION="$BAKED_VERSION"
fi

if [[ -z "$VERSION" ]]; then
  info "Resolving latest stable release from ${RELEASE_API_LATEST}..."
  if ! VERSION="$(resolve_latest_release)"; then
    fail "Could not resolve the latest release. Pass --version <tag> explicitly or set SPRING_VOYAGE_VERSION."
  fi
fi

# Normalise: accept '1.0.0', 'v1.0.0', or 'spring-voyage-v1.0.0' as input.
SEMVER="${VERSION#spring-voyage-}"
SEMVER="${SEMVER#v}"
TAG="spring-voyage-v${SEMVER}"
RELEASE_VERSION="${SEMVER}"
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
# Download release archive + SHA256SUMS
# ---------------------------------------------------------------------------
# As of #2243 there is one per-RID operator archive containing bundle/,
# cli/, and dispatcher/ (was three separate archives: bundle + cli +
# dispatcher). The archive is staged in the release as
# `spring-voyage-<v>-<rid>.tar.gz`.
header "Downloading release archive"

ARCHIVE_ASSET="spring-voyage-${RELEASE_VERSION}-${RID}.tar.gz"

download_asset() {
  local name="$1"
  local url="${RELEASE_DOWNLOAD_PREFIX}/${TAG}/${name}"
  local dest="${DOWNLOAD_DIR}/${name}"
  info "Downloading ${name}..."
  if ! curl -fSL --retry 3 --retry-delay 2 -o "${dest}" "${url}"; then
    fail "Download failed: ${url}"
  fi
}

download_asset "SHA256SUMS"
download_asset "${ARCHIVE_ASSET}"
ok "All assets downloaded"

# ---------------------------------------------------------------------------
# Verify checksum
# ---------------------------------------------------------------------------
header "Verifying SHA256 checksum"

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

verify_one "${ARCHIVE_ASSET}"

# ---------------------------------------------------------------------------
# Extract
# ---------------------------------------------------------------------------
header "Extracting archive"
# Archive contains bundle/, cli/, and dispatcher/ subdirectories — extract
# into RELEASE_DIR so the existing symlink/locate logic finds them at
# their expected paths (RELEASE_DIR/{bundle,cli,dispatcher}).
rm -rf "${BUNDLE_DIR}" "${DISPATCHER_DIR}" "${CLI_DIR}"
tar -xzf "${DOWNLOAD_DIR}/${ARCHIVE_ASSET}" -C "${RELEASE_DIR}"
[[ -d "${BUNDLE_DIR}" ]] || fail "Archive did not produce expected layout at ${BUNDLE_DIR}."
[[ -d "${DISPATCHER_DIR}" ]] || fail "Archive did not produce expected layout at ${DISPATCHER_DIR}."
[[ -d "${CLI_DIR}" ]] || fail "Archive did not produce expected layout at ${CLI_DIR}."
ok "Archive extracted -> ${RELEASE_DIR}"

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

# Generate secrets the same way eng/deploy/setup.sh does, just without
# prompting. We mirror the entropy and encoding choices so an operator can
# audit one path and trust the other.
POSTGRES_PASSWORD="$(openssl rand -hex 8 | head -c 16)"
SPRING_SECRETS_AES_KEY="$(openssl rand -base64 32 | tr -d '\n')"

# Re-install over an existing spring.env (--force): preserve the two secrets
# that must NEVER rotate silently. SPRING_SECRETS_AES_KEY decrypts every secret
# in the state store — regenerating it orphans all of them. POSTGRES_PASSWORD is
# baked into the postgres data volume on first init — regenerating it breaks auth
# against the (preserved) volume. deploy.sh init refuses to clobber the AES key
# for the same reason; mirror that guarantee here so --force can't quietly brick
# an install's secrets and database.
if [[ -f "${SPRING_ENV_FILE}" ]]; then
  existing_aes="$(awk '/^SPRING_SECRETS_AES_KEY=/ { sub(/^SPRING_SECRETS_AES_KEY=/, ""); v=$0 } END { print v }' "${SPRING_ENV_FILE}" 2>/dev/null || true)"
  existing_pg="$(awk '/^POSTGRES_PASSWORD=/ { sub(/^POSTGRES_PASSWORD=/, ""); v=$0 } END { print v }' "${SPRING_ENV_FILE}" 2>/dev/null || true)"
  if [[ -n "${existing_aes}" && "${existing_aes}" != "REPLACE_ME_WITH_BASE64_32_BYTES" ]]; then
    SPRING_SECRETS_AES_KEY="${existing_aes}"
    info "Preserving existing SPRING_SECRETS_AES_KEY from ${SPRING_ENV_FILE} (regenerating it would orphan every encrypted secret)."
  fi
  if [[ -n "${existing_pg}" ]]; then
    POSTGRES_PASSWORD="${existing_pg}"
    info "Preserving existing POSTGRES_PASSWORD from ${SPRING_ENV_FILE} (regenerating it would break auth against the existing database volume)."
  fi
fi

# Public scheme + authority for the OAuth redirect and webhook URLs. These must
# match what Caddy actually serves: the Caddyfile site address is
# {$DEPLOY_SCHEME:http}://{$DEPLOY_HOSTNAME}, and Caddy terminates TLS only for a
# real FQDN (automatic Let's Encrypt). A loopback host (localhost, *.localhost,
# 127.0.0.1, ::1) has no public certificate, so it is served over plain HTTP —
# a browser redirect to https://localhost would hit Caddy's 443 with no matching
# TLS site and the handshake is reset (ERR_CONNECTION_RESET). Derive the scheme
# from the hostname and reuse it everywhere (env, redirect, webhook, summary).
#
# Include the host-published port in the authority when it is not the scheme
# default (80/443) so the URLs resolve on a port-remapped install. Both URLs are
# handed to `spring github-app register` below so the GitHub App's webhook URL
# and callback_urls point at THIS deployment — not the CLI's localhost:5000
# default — and callback_urls matches GitHub__OAuth__RedirectUri exactly (GitHub
# validates the OAuth redirect_uri against it byte-for-byte).
# TLS decision (ADR-0068). A loopback host never gets TLS (no public cert;
# https://localhost would reset). For a custom hostname we do NOT blindly enable
# Let's Encrypt — if DNS isn't pointed here (or Caddy was remapped off 80/443)
# the ACME challenge fails and the site is left unserved (#2928). Probe
# readiness: auto-enable Let's Encrypt only when verifiable, else keep the safe
# HTTP default and offer private HTTPS via Caddy's local CA (TLS_MODE=internal),
# which needs no public DNS or open ports. TLS_MODE is written to spring.env.
TLS_MODE=""
case "${DEPLOY_HOSTNAME}" in
  localhost | *.localhost | 127.0.0.1 | ::1)
    DEPLOY_SCHEME="http"
    ;;
  *)
    if acme_ready "${DEPLOY_HOSTNAME}"; then
      DEPLOY_SCHEME="https"; TLS_MODE="auto"
      ok "DNS for ${DEPLOY_HOSTNAME} resolves to this host on 80/443 — enabling automatic Let's Encrypt."
    elif [[ "$ASSUME_YES" -eq 0 ]] && { [[ -t 0 ]] || [[ -r /dev/tty ]]; }; then
      warn "${DEPLOY_HOSTNAME} doesn't verifiably resolve to this host, so automatic Let's Encrypt may fail."
      printf '  Choose TLS:\n' >&2
      printf '    1) Plain HTTP for now (default) — enable TLS later by fixing DNS + re-running, or set TLS_MODE in spring.env\n' >&2
      printf '    2) Private HTTPS via Caddy'\''s local CA — works now; browsers warn until you trust the CA\n' >&2
      printf '    3) Public Let'\''s Encrypt anyway — only if this host really is reachable on 80/443 (e.g. behind NAT/LB)\n' >&2
      printf '  %sTLS choice%s [1]: ' "${BOLD}" "${NC}" >&2
      if [[ -t 0 ]]; then
        IFS= read -r tls_answer || tls_answer=""
      elif [[ -r /dev/tty ]]; then
        IFS= read -r tls_answer </dev/tty || tls_answer=""
      else
        tls_answer=""
      fi
      case "${tls_answer}" in
        2) DEPLOY_SCHEME="https"; TLS_MODE="internal" ;;
        3) DEPLOY_SCHEME="https"; TLS_MODE="auto" ;;
        *) DEPLOY_SCHEME="http" ;;
      esac
    else
      DEPLOY_SCHEME="http"
      warn "${DEPLOY_HOSTNAME} not verifiably reachable for Let's Encrypt; defaulting to HTTP. Set DEPLOY_SCHEME=https plus TLS_MODE=auto (public) or TLS_MODE=internal (private CA) in ${SPRING_ENV_FILE} and re-run to enable TLS."
    fi
    ;;
esac
if [[ "${DEPLOY_SCHEME}" == "https" ]]; then
  REDIRECT_AUTHORITY="$(http_authority "${DEPLOY_HOSTNAME}" "${CADDY_HTTPS_PORT:-443}" 443)"
else
  REDIRECT_AUTHORITY="$(http_authority "${DEPLOY_HOSTNAME}" "${CADDY_HTTP_PORT:-80}" 80)"
fi
REDIRECT_URI="${DEPLOY_SCHEME}://${REDIRECT_AUTHORITY}/api/v1/tenant/connectors/github/oauth/callback"
WEBHOOK_URL="${DEPLOY_SCHEME}://${REDIRECT_AUTHORITY}/api/v1/webhooks/github"
# Default App name suggestion; the operator can rename it on GitHub. Required by
# the CLI and referenced in the re-run hints below, so compute it once here.
DEFAULT_APP_NAME="spring-voyage-${DEPLOY_HOSTNAME//[^A-Za-z0-9]/-}"
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
  printf '# Written by eng/install/install.sh on %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
  printf '# Edit below to reconfigure; values here shadow the spring.env.example defaults.\n'
  printf '# ---------------------------------------------------------------------------\n'
  printf 'SPRING_IMAGE_TAG=%s\n' "${RELEASE_VERSION}"
  printf 'SPRING_PLATFORM_IMAGE=%s\n' "${PLATFORM_IMAGE}"
  printf 'POSTGRES_PASSWORD=%s\n' "${POSTGRES_PASSWORD}"
  printf 'SPRING_SECRETS_AES_KEY=%s\n' "${SPRING_SECRETS_AES_KEY}"
  printf 'DEPLOY_HOSTNAME=%s\n' "${DEPLOY_HOSTNAME}"
  printf 'DEPLOY_SCHEME=%s\n' "${DEPLOY_SCHEME}"
  if [[ -n "${TLS_MODE}" ]]; then
    printf 'TLS_MODE=%s\n' "${TLS_MODE}"
  fi
  printf 'GitHub__OAuth__RedirectUri=%s\n' "${REDIRECT_URI}"
  printf 'Dapr__Sidecar__DelegatedSpringVoyageAgentComponentsPath=%s\n' "${DAPR_COMPONENTS_PATH}"
  printf 'SPRING_DISPATCHER_BIN=%s\n' "${DISPATCHER_BIN}"
  # Host-port overrides — emitted only when the pre-flight remapped a default
  # off a conflict (or the operator pre-set one). When all four are at their
  # defaults nothing is written and the spring.env.example values stand.
  if [[ "${CADDY_HTTP_PORT:-80}" != "80" ]]; then
    printf 'CADDY_HTTP_PORT=%s\n' "${CADDY_HTTP_PORT}"
  fi
  if [[ "${CADDY_HTTPS_PORT:-443}" != "443" ]]; then
    printf 'CADDY_HTTPS_PORT=%s\n' "${CADDY_HTTPS_PORT}"
  fi
  if [[ "${SPRING_DISPATCHER_PORT:-8090}" != "8090" ]]; then
    printf 'SPRING_DISPATCHER_PORT=%s\n' "${SPRING_DISPATCHER_PORT}"
  fi
  if [[ "${Mcp__Port:-5050}" != "5050" ]]; then
    printf 'Mcp__Port=%s\n' "${Mcp__Port}"
  fi
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
  header "GitHub connector (optional)"
  info "The GitHub connector has two auth paths — registering an App is optional:"
  info "  • GitHub App — for repositories you OWN/operate (bot identity + webhooks)."
  info "                 The CLI can register it now with a single browser click."
  info "  • PAT        — for contributing to a repository you do NOT own (e.g. an"
  info "                 open-source project). No App, no OAuth: you create a token"
  info "                 later. This is the simplest setup."
  info "Skip if you'll use a PAT (or are just trying things out); you can register an"
  info "App anytime with \`spring github-app register\`."
  printf '  %sRegister a GitHub App now?%s [y/N]: ' "${BOLD}" "${NC}" >&2
  if [[ -t 0 ]]; then
    IFS= read -r answer || answer=""
  elif [[ -r /dev/tty ]]; then
    IFS= read -r answer </dev/tty || answer=""
  else
    answer=""
  fi
  GITHUB_APP_PROMPTED=1
  case "${answer:-N}" in
    [Yy]|[Yy][Ee][Ss])
      info "Running: spring github-app register --name ${DEFAULT_APP_NAME} --webhook-url ${WEBHOOK_URL} --oauth-callback-url ${REDIRECT_URI} --env-path ${SPRING_ENV_FILE} --write-env"
      if "${BIN_DIR}/spring" github-app register \
            --name "${DEFAULT_APP_NAME}" \
            --webhook-url "${WEBHOOK_URL}" \
            --oauth-callback-url "${REDIRECT_URI}" \
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
        info "Re-run later:"
        info "  spring github-app register --name ${DEFAULT_APP_NAME} --webhook-url ${WEBHOOK_URL} --oauth-callback-url ${REDIRECT_URI} --env-path ${SPRING_ENV_FILE} --write-env"
      fi
      ;;
    *)
      info "No GitHub App registered. Connect GitHub when ready — see the two options"
      info "(PAT for repos you don't own; App for repos you do) in the summary below."
      ;;
  esac
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
if [[ "$STACK_STARTED" -eq 1 ]]; then
  header "Install complete"
else
  header "Install incomplete — stack did not start"
fi

# Reuse the scheme + authority derived above so the printed portal URL matches
# the scheme Caddy serves and the OAuth callback registered on the GitHub App.
WEB_URL="${DEPLOY_SCHEME}://${REDIRECT_AUTHORITY}"

# Point the bundled `spring` CLI at this deployment's API. Otherwise the CLI
# falls back to a built-in http://localhost:5000 dev default no container install
# exposes — Caddy fronts the API on the resolved host port (e.g.
# http://localhost:8081 after a privileged-port remap), so the operator's very
# first `spring …` command fails with "Connection refused" (#3092). We delegate
# the write to the freshly-installed CLI: `config set endpoint` merges into
# ~/.spring/config.json, refreshing a changed host port on re-install (#3091)
# while preserving any stored auth token — no fragile bash JSON surgery, and the
# CLI locks the file down to 0600. Run-time resolution stays SPRING_API_URL >
# config.json, so an operator override via the env var always wins.
SPRING_CLI_CONFIG_FILE="${HOME}/.spring/config.json"
CLI_ENDPOINT_CONFIGURED=0
if "${CLI_BIN}" config set endpoint "${WEB_URL}" >/dev/null 2>&1; then
  CLI_ENDPOINT_CONFIGURED=1
fi

cat <<EOF

  Install root:        ${INSTALL_ROOT}
  spring.env:          ${SPRING_ENV_FILE}
  Current symlink:     ${INSTALL_ROOT}/current -> ${BUNDLE_DIR}
  CLI:                 ${BIN_DIR}/spring
  Wrapper:             ${BIN_DIR}/voyage
  Logs (containers):   podman logs spring-api (and friends)
  Logs (dispatcher):   ${INSTALL_ROOT}/host/spring-dispatcher.log
  Web URL:             ${WEB_URL}
  API endpoint:        ${WEB_URL}   (spring CLI; override with SPRING_API_URL)

  Day-2 commands:
    voyage status               # install version, container/dispatcher health, web URL
    voyage logs [service]       # tail container logs (or 'dispatcher' for the host process)
    voyage restart              # restart the stack
    voyage version              # print the installed version + image tag

  To tear down later:
    voyage uninstall            # preserves spring.env + workspaces
    voyage uninstall --purge    # factory reset

EOF

if [[ "${TLS_MODE}" == "internal" ]]; then
  info "TLS: serving HTTPS with Caddy's local CA (self-signed). Trust it on each client:"
  info "  podman cp spring-caddy:/data/caddy/pki/authorities/local/root.crt ./spring-root.crt"
  info "  then add spring-root.crt to the system / browser trust store."
fi

if [[ "$CLI_ENDPOINT_CONFIGURED" -eq 1 ]]; then
  info "Pointed the spring CLI at ${WEB_URL} (${SPRING_CLI_CONFIG_FILE})."
else
  warn "Could not set the spring CLI endpoint automatically. Point it at the API by hand: spring config set endpoint ${WEB_URL}  (or export SPRING_API_URL=${WEB_URL})"
fi

if [[ "$PATH_WARNING_NEEDED" -eq 1 ]]; then
  warn "Don't forget to add ${BIN_DIR} to your PATH (see the export line above)."
fi

if [[ "$GITHUB_APP_PROMPTED" -eq 0 || "$GITHUB_APP_CONFIGURED" -eq 0 ]]; then
  cat <<EOF
  Next: connect GitHub (optional — pick the path that fits)
    • Contribute to a repo you DON'T own — use a PAT (no App, no OAuth):
        spring secret create --scope tenant github-pat --value 'ghp_...'
        spring connector bind --unit <unit> --type github --repo <owner>/<repo> --pat-secret-name github-pat
    • Operate a repo you own — register the per-deployment GitHub App:
        spring github-app register --name ${DEFAULT_APP_NAME} --webhook-url ${WEBHOOK_URL} --oauth-callback-url ${REDIRECT_URI} --env-path ${SPRING_ENV_FILE} --write-env
        # Then: SPRING_ENV_FILE=${SPRING_ENV_FILE} ${DEPLOY_SH} restart
    Which to use: https://github.com/${REPO_OWNER}/${REPO_NAME}/blob/main/docs/guide/operator/github-connector-auth.md

EOF
fi

cat <<EOF
  Next: configure LLM provider credentials
    spring secret create --scope tenant anthropic-api-key --value 'sk-ant-...'
    spring secret create --scope tenant openai-api-key    --value 'sk-...'

  Full operator docs: https://github.com/${REPO_OWNER}/${REPO_NAME}/blob/main/docs/guide/operator/deployment.md

EOF
