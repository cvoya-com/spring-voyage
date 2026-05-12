#!/usr/bin/env bash
# Spring Voyage — uninstall counterpart to install.sh (ADR-0042).
#
# Default mode: tear the stack down, remove install-root assets owned by
#   install.sh, and PRESERVE operator data (spring.env, host state,
#   workspaces). The default-mode contract is "you keep your data."
#
# --purge: factory reset. Also remove spring.env, host state, and
#   workspaces. The default mode + --purge are documented at install
#   time so an operator never destroys data accidentally.
#
# Usage:
#   uninstall.sh [--purge] [--yes] [--force]
#   spring-voyage uninstall [--purge] [--yes] [--force]
#
# Options:
#   --purge   Also remove spring.env, ~/.spring-voyage/host/, and
#             ~/.spring-voyage/workspaces/. Default mode preserves them.
#   --yes     Skip the interactive confirmation prompt.
#   --force   Tear down even if `deploy.sh` reports running containers
#             (the default mode refuses to proceed in that case).
#   -h/--help Show this help.
#
# Idempotent: missing files are not errors.

set -euo pipefail

# ---------------------------------------------------------------------------
# Defaults
# ---------------------------------------------------------------------------
INSTALL_ROOT="${SPRING_VOYAGE_HOME:-${HOME}/.spring-voyage}"
BIN_DIR="${SPRING_VOYAGE_BIN_DIR:-${HOME}/.local/bin}"
SPRING_ENV_FILE="${SPRING_ENV_FILE:-${INSTALL_ROOT}/spring.env}"

PURGE=0
ASSUME_YES=0
FORCE=0

# ---------------------------------------------------------------------------
# Colour helpers
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

usage() {
  cat <<'EOF'
Spring Voyage — uninstall.

Usage:
  uninstall.sh [--purge] [--yes] [--force]

Default mode (preserves operator data):
  - Stops containers (deploy.sh down + clean).
  - Removes ~/.spring-voyage/releases/, ~/.spring-voyage/current.
  - Removes ~/.local/bin/spring and ~/.local/bin/spring-voyage.
  - PRESERVES: spring.env, ~/.spring-voyage/host/, ~/.spring-voyage/workspaces/.

--purge:
  - All of the above + removes spring.env, host/, workspaces/.
  - Factory reset.

--yes:     Skip confirmation prompt.
--force:   Tear down even when deploy.sh reports running containers.
-h, --help Show this message.

Environment overrides:
  SPRING_VOYAGE_HOME    Install root (default ~/.spring-voyage).
  SPRING_ENV_FILE       Path to spring.env (default <install-root>/spring.env).
EOF
}

# ---------------------------------------------------------------------------
# Arg parsing
# ---------------------------------------------------------------------------
while [[ $# -gt 0 ]]; do
  case "$1" in
    --purge) PURGE=1; shift ;;
    --yes|-y) ASSUME_YES=1; shift ;;
    --force) FORCE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) fail "unknown option: $1 (try --help)" ;;
  esac
done

# ---------------------------------------------------------------------------
# Refuse to run as root
# ---------------------------------------------------------------------------
if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
  fail "Refusing to run as root. Re-run as the user who installed Spring Voyage."
fi

# ---------------------------------------------------------------------------
# Confirmation prompt
# ---------------------------------------------------------------------------
header "Spring Voyage uninstall"
if [[ "$PURGE" -eq 1 ]]; then
  info "Mode: --purge (factory reset)"
  info "Will remove:"
  info "  - Containers, volumes, networks, images (via deploy.sh clean)"
  info "  - ${INSTALL_ROOT}/releases/"
  info "  - ${INSTALL_ROOT}/current"
  info "  - ${BIN_DIR}/spring, ${BIN_DIR}/spring-voyage"
  info "  - ${SPRING_ENV_FILE}"
  info "  - ${INSTALL_ROOT}/host/"
  info "  - ${INSTALL_ROOT}/workspaces/"
else
  info "Mode: default (preserves operator data)"
  info "Will remove:"
  info "  - Containers, volumes, networks, images (via deploy.sh clean)"
  info "  - ${INSTALL_ROOT}/releases/"
  info "  - ${INSTALL_ROOT}/current"
  info "  - ${BIN_DIR}/spring, ${BIN_DIR}/spring-voyage"
  info "Will PRESERVE:"
  info "  - ${SPRING_ENV_FILE}"
  info "  - ${INSTALL_ROOT}/host/"
  info "  - ${INSTALL_ROOT}/workspaces/"
fi

if [[ "$ASSUME_YES" -eq 0 ]]; then
  printf '\n  %sProceed?%s [y/N]: ' "${BOLD}" "${NC}" >&2
  if [[ -t 0 ]]; then
    IFS= read -r answer || answer=""
  elif [[ -r /dev/tty ]]; then
    IFS= read -r answer </dev/tty || answer=""
  else
    answer=""
  fi
  case "${answer:-N}" in
    [Yy]|[Yy][Ee][Ss]) : ;;
    *) fail "Aborted by user." ;;
  esac
fi

# ---------------------------------------------------------------------------
# Resolve bundle for deploy.sh access
# ---------------------------------------------------------------------------
header "Tearing down the stack"

BUNDLE_DIR=""
if [[ -L "${INSTALL_ROOT}/current" ]]; then
  BUNDLE_DIR="$(readlink "${INSTALL_ROOT}/current" 2>/dev/null || true)"
elif [[ -d "${INSTALL_ROOT}/current" ]]; then
  BUNDLE_DIR="${INSTALL_ROOT}/current"
fi

# Best-effort: also look in the most-recent release dir if `current` is gone.
if [[ ! -d "${BUNDLE_DIR}" && -d "${INSTALL_ROOT}/releases" ]]; then
  BUNDLE_DIR="$(find "${INSTALL_ROOT}/releases" -maxdepth 2 -type d -name bundle 2>/dev/null | sort | tail -n1)"
fi

if [[ -n "$BUNDLE_DIR" && -x "${BUNDLE_DIR}/deploy.sh" ]]; then
  DEPLOY_SH="${BUNDLE_DIR}/deploy.sh"

  # Check for running containers and a live dispatcher when --force is not set.
  if [[ "$FORCE" -eq 0 ]]; then
    running=""
    if command -v podman >/dev/null 2>&1; then
      running="$(podman ps --format '{{.Names}}' 2>/dev/null | grep -E '^spring-' || true)"
    fi
    dispatcher_pid_file="${INSTALL_ROOT}/host/spring-dispatcher.pid"
    dispatcher_live_pid=""
    if [[ -f "${dispatcher_pid_file}" ]]; then
      pid="$(tr -d '[:space:]' < "${dispatcher_pid_file}" 2>/dev/null || true)"
      if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
        dispatcher_live_pid="$pid"
      fi
    fi
    if [[ -n "$running" || -n "$dispatcher_live_pid" ]]; then
      warn "Spring Voyage services still running:"
      if [[ -n "$running" ]]; then
        printf '%s\n' "$running" | sed 's/^/      - container: /' >&2
      fi
      if [[ -n "$dispatcher_live_pid" ]]; then
        printf '      - dispatcher: PID %s (%s)\n' "$dispatcher_live_pid" "$dispatcher_pid_file" >&2
      fi
      info "We will attempt deploy.sh down + clean to stop them. Rerun with --force to bypass this check."
    fi
  fi

  info "SPRING_ENV_FILE=${SPRING_ENV_FILE} ${DEPLOY_SH} down"
  SPRING_ENV_FILE="${SPRING_ENV_FILE}" "${DEPLOY_SH}" down || warn "deploy.sh down returned non-zero; continuing."

  info "SPRING_ENV_FILE=${SPRING_ENV_FILE} ${DEPLOY_SH} clean"
  SPRING_ENV_FILE="${SPRING_ENV_FILE}" "${DEPLOY_SH}" clean || warn "deploy.sh clean returned non-zero; continuing."
  ok "Stack torn down"
else
  warn "No deploy.sh found under ${INSTALL_ROOT}; skipping container teardown."
  info "If containers are still running, stop them manually with: podman stop \$(podman ps -q --filter name=spring-)"
  info "Also stop the host dispatcher: kill \$(cat ~/.spring-voyage/host/spring-dispatcher.pid) 2>/dev/null"
fi

# ---------------------------------------------------------------------------
# Remove install-root assets that install.sh owns
# ---------------------------------------------------------------------------
header "Removing installed files"

rm_path() {
  local target="$1" label="$2"
  if [[ -L "$target" ]]; then
    if rm -f "$target"; then ok "removed ${label} (symlink) ${target}"; else warn "failed to remove ${target}"; fi
  elif [[ -d "$target" ]]; then
    if rm -rf "$target"; then ok "removed ${label} (dir) ${target}"; else warn "failed to remove ${target}"; fi
  elif [[ -e "$target" ]]; then
    if rm -f "$target"; then ok "removed ${label} ${target}"; else warn "failed to remove ${target}"; fi
  else
    info "(not present) ${target}"
  fi
}

rm_path "${INSTALL_ROOT}/releases" "releases"
rm_path "${INSTALL_ROOT}/current"  "current"
rm_path "${BIN_DIR}/spring"        "spring binary"
rm_path "${BIN_DIR}/spring-voyage" "spring-voyage wrapper"

# ---------------------------------------------------------------------------
# Purge: also remove operator data
# ---------------------------------------------------------------------------
if [[ "$PURGE" -eq 1 ]]; then
  header "Removing operator data (--purge)"
  rm_path "${SPRING_ENV_FILE}"        "spring.env"
  rm_path "${INSTALL_ROOT}/host"      "host state"
  rm_path "${INSTALL_ROOT}/workspaces" "workspaces"
  # Also remove ~/.spring-voyage/deployment (local-ollama profile) and
  # the install root if it ends up empty.
  rm_path "${INSTALL_ROOT}/deployment" "deployment cache"

  # Remove install root itself if empty.
  if [[ -d "${INSTALL_ROOT}" ]]; then
    if ! find "${INSTALL_ROOT}" -mindepth 1 -print -quit | grep -q .; then
      if rmdir "${INSTALL_ROOT}" 2>/dev/null; then
        ok "removed empty install root ${INSTALL_ROOT}"
      fi
    fi
  fi
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
header "Uninstall complete"
if [[ "$PURGE" -eq 1 ]]; then
  printf '\n  Factory reset complete. Nothing of Spring Voyage remains under %s.\n\n' "${INSTALL_ROOT}"
else
  printf '\n  Default uninstall complete. The following operator data was preserved:\n\n'
  [[ -f "${SPRING_ENV_FILE}" ]]            && printf '    %s\n' "${SPRING_ENV_FILE}"
  [[ -d "${INSTALL_ROOT}/host" ]]          && printf '    %s\n' "${INSTALL_ROOT}/host/"
  [[ -d "${INSTALL_ROOT}/workspaces" ]]    && printf '    %s\n' "${INSTALL_ROOT}/workspaces/"
  printf '\n  To remove these too, re-run with --purge.\n\n'
fi
