#!/usr/bin/env bash
# Spring Voyage — self-host setup wizard.
#
# Creates eng/config/spring.env from spring.env.example and collects the
# secrets every self-hosted deployment must supply.  Values that can be
# derived automatically (AES key, OAuth redirect URI, Dapr components path)
# are written without prompting.
#
# Usage:
#   ./eng/deploy/setup.sh
#
# Re-running is safe: you are asked whether to overwrite an existing spring.env.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

# setup.sh is a source-tree tool; it writes eng/config/spring.env and requires
# a full checkout. Detect a bundle install and redirect to deploy.sh init.
if [[ -f "${SCRIPT_DIR}/manifest.json" ]]; then
  printf 'setup.sh requires a source checkout and cannot run from an installed bundle.\n' >&2
  printf 'To bootstrap spring.env in an installed deployment, run: deploy.sh init\n' >&2
  exit 1
fi

CONFIG_DIR="${REPO_ROOT}/eng/config"
ENV_EXAMPLE="${CONFIG_DIR}/spring.env.example"
ENV_FILE="${CONFIG_DIR}/spring.env"

# ---------------------------------------------------------------------------
# Colour helpers (degrade gracefully when not a TTY)
# ---------------------------------------------------------------------------
if [[ -t 1 ]]; then
  BOLD='\033[1m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; RED='\033[0;31m'; NC='\033[0m'
else
  BOLD=''; GREEN=''; YELLOW=''; CYAN=''; RED=''; NC=''
fi

header()  { printf "\n${BOLD}${CYAN}%s${NC}\n" "$1"; printf "${CYAN}%s${NC}\n" "$(printf '─%.0s' $(seq 1 ${#1}))"; }
ok()      { printf "  ${GREEN}✓${NC}  %s\n" "$*"; }
info()    { printf "      %s\n" "$*"; }
warn()    { printf "  ${YELLOW}!${NC}  %s\n" "$*"; }
fail()    { printf "  ${RED}✗${NC}  %s\n" "$*"; }

# ---------------------------------------------------------------------------
# preflight — verify every tool required for setup / build / deploy
# ---------------------------------------------------------------------------
# Hard requirements abort the wizard; soft requirements emit a warning only.
#
# Tool                  Why needed
# ─────────────────     ──────────────────────────────────────────────────────
# docker/podman 4.4+    image builds (platform Dockerfile uses COPY --parents)
# openssl               AES key generation; deploy.sh init key rotation
# envsubst              deploy.sh expands variables in the resolved spring.env
# dotnet 10 SDK         spring-voyage-host.sh builds and runs the dispatcher
# python3   (soft)      gen_hex prefers it; falls back to openssl automatically
# curl      (soft)      health probes; deploy.sh skips probe gracefully if absent
# ---------------------------------------------------------------------------
preflight() {
  local _errs=0

  # Helper: record a hard-requirement failure.
  # Usage: _err "headline" ["hint line" ...]
  _err() { fail "$1"; shift; while [[ $# -gt 0 ]]; do info "$1"; shift; done; _errs=$(( _errs + 1 )); }

  # Detect WSL2 for targeted install hints.
  local _wsl2=0
  grep -qiE 'microsoft|wsl' /proc/version 2>/dev/null && _wsl2=1

  header "Requirements"
  [[ $_wsl2 -eq 1 ]] && info "Detected: Windows Subsystem for Linux 2 (WSL2)"

  # ---- container runtime ---------------------------------------------------
  # The platform image (eng/build/Dockerfile) uses COPY --parents, which
  # requires BuildKit. Docker 23.0+ enables BuildKit by default; Podman needs
  # buildah 1.28+ (shipped with Podman 4.4).
  if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    local _dv
    _dv=$(docker version --format '{{.Client.Version}}' 2>/dev/null || true)
    local _dmaj="${_dv%%.*}"
    if [[ "$_dmaj" =~ ^[0-9]+$ ]] && (( _dmaj < 23 )); then
      _err "docker ${_dv} — 23.0+ required" \
           "The platform Dockerfile uses COPY --parents (BuildKit, enabled by default in Docker 23+)." \
           "Upgrade: https://docs.docker.com/engine/install/"
    else
      ok "docker ${_dv:-[version unknown]}"
    fi
  elif command -v podman >/dev/null 2>&1; then
    local _pv
    _pv=$(podman --version 2>/dev/null | grep -oE '[0-9]+\.[0-9]+(\.[0-9]+)?' | head -1)
    local _pmaj _pmin
    _pmaj=$(printf '%s' "$_pv" | cut -d. -f1)
    _pmin=$(printf '%s' "$_pv" | cut -d. -f2)
    if [[ "$_pmaj" =~ ^[0-9]+$ ]] && [[ "$_pmin" =~ ^[0-9]+$ ]] \
    && { (( _pmaj < 4 )) || { (( _pmaj == 4 )) && (( _pmin < 4 )); }; }; then
      _err "podman ${_pv} — 4.4+ required" \
           "The platform Dockerfile uses COPY --parents, which requires buildah 1.28+ (Podman 4.4+)."
      if [[ $_wsl2 -eq 1 ]]; then
        info "On WSL2, install from the official Podman repository (Ubuntu's default is too old):"
        info "  https://podman.io/docs/installation#ubuntu"
      else
        info "Upgrade: https://podman.io/docs/installation"
      fi
    else
      ok "podman ${_pv:-[version unknown]}"
    fi
  else
    _err "no container runtime found — install docker (23.0+) or podman (4.4+)" \
         "Docker:  https://docs.docker.com/engine/install/" \
         "Podman:  https://podman.io/docs/installation"
    if [[ $_wsl2 -eq 1 ]]; then
      info "On WSL2: Docker Desktop (WSL2 backend) or native rootless Podman are both supported."
    fi
  fi

  # ---- openssl -------------------------------------------------------------
  # gen_hex uses openssl as a fallback and deploy.sh init requires it for key
  # rotation. No further fallback exists.
  if command -v openssl >/dev/null 2>&1; then
    ok "openssl"
  else
    _err "openssl not found" \
         "Required for AES key generation and by 'deploy.sh init'." \
         "Install: apt-get install openssl      (Debian / Ubuntu)" \
         "         brew install openssl          (macOS)"
  fi

  # ---- envsubst (gettext-base) ---------------------------------------------
  # deploy.sh pipes spring.env through envsubst to expand variable references
  # before passing it to podman via --env-file.
  if command -v envsubst >/dev/null 2>&1; then
    ok "envsubst"
  else
    _err "envsubst not found" \
         "Required by deploy.sh to expand variables in spring.env." \
         "Install: apt-get install gettext-base  (Debian / Ubuntu)" \
         "         brew install gettext           (macOS)"
  fi

  # ---- .NET 10 SDK ---------------------------------------------------------
  # spring-voyage-host.sh compiles and launches the spring-dispatcher, which
  # is a .NET 10 application. The repo's global.json pins the SDK channel.
  if command -v dotnet >/dev/null 2>&1; then
    local _dnv
    _dnv=$(cd "${REPO_ROOT}" && dotnet --version 2>/dev/null || true)
    if [[ -z "$_dnv" ]]; then
      _err "dotnet found but 'dotnet --version' failed" \
           "The repo's global.json requires .NET 10 SDK; no compatible version was found." \
           "Install .NET 10 SDK:" \
           "  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0"
    else
      local _dnmaj="${_dnv%%.*}"
      if [[ "$_dnmaj" =~ ^[0-9]+$ ]] && (( _dnmaj < 10 )); then
        _err "dotnet ${_dnv} — .NET 10 SDK required" \
             "The spring-dispatcher host process requires .NET 10." \
             "Install .NET 10 SDK:" \
             "  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0"
      else
        ok "dotnet ${_dnv}"
      fi
    fi
  else
    _err "dotnet not found — .NET 10 SDK required" \
         "spring-voyage-host.sh builds and runs the dispatcher, which is a .NET 10 process." \
         "Install .NET 10 SDK:" \
         "  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0"
  fi

  # ---- python3 (soft) ------------------------------------------------------
  # gen_hex prefers python3 for entropy; openssl is the automatic fallback.
  if command -v python3 >/dev/null 2>&1; then
    local _pyv
    _pyv=$(python3 --version 2>&1 | grep -oE '[0-9]+\.[0-9]+(\.[0-9]+)?' | head -1 || true)
    ok "python3 ${_pyv:-[version unknown]}"
  else
    warn "python3 not found — AES key generation will use openssl instead (safe)"
  fi

  # ---- curl (soft) ---------------------------------------------------------
  # spring-voyage-host.sh uses curl for dispatcher health probes; deploy.sh
  # uses it for Ollama reachability checks. Both scripts degrade gracefully
  # (blind sleep) when curl is absent.
  if command -v curl >/dev/null 2>&1; then
    ok "curl"
  else
    warn "curl not found — health probes will fall back to blind sleeps"
    info "Install: apt-get install curl"
  fi

  # ---- summary -------------------------------------------------------------
  printf '\n'
  if (( _errs > 0 )); then
    printf "  ${RED}${BOLD}%d requirement(s) not met.${NC}  Fix the ${RED}✗${NC} items above, then re-run setup.sh.\n\n" "$_errs" >&2
    exit 1
  fi
  ok "All requirements satisfied."
}

prompt() {
  local label="$1" default="${2:-}"
  if [[ -n "$default" ]]; then
    printf "  ${BOLD}%-36s${NC} [%s]: " "$label" "$default" >&2
  else
    printf "  ${BOLD}%-36s${NC} " "$label" >&2
  fi
  local val; IFS= read -r val </dev/tty
  printf '%s' "${val:-$default}"
}

prompt_secret() {
  local label="$1"
  printf "  ${BOLD}%-36s${NC} " "$label" >&2
  local val; IFS= read -r -s val </dev/tty; printf '\n' >&2
  printf '%s' "$val"
}

prompt_multiline() {
  # Reads until the user enters a line containing only "END" (case-insensitive).
  local label="$1"
  printf "  ${BOLD}%s${NC}\n" "$label" >&2
  printf "  (paste all lines; type END on its own line when done)\n" >&2
  local buf="" line
  while IFS= read -r line </dev/tty; do
    [[ "${line,,}" == "end" ]] && break
    buf+="${line}"$'\n'
  done
  printf '%s' "$buf"
}

gen_hex() { python3 -c "import os; print(os.urandom($1).hex())" 2>/dev/null || openssl rand -hex "$1"; }

append() {
  # append KEY=VALUE to ENV_FILE, optionally wrapping value in single quotes
  local key="$1" value="$2" quote="${3:-}"
  printf '%s=%s%s%s\n' "$key" "$quote" "$value" "$quote" >> "${ENV_FILE}"
}

resolve_container_cli() {
  if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    printf 'docker'
  elif command -v podman >/dev/null 2>&1; then
    printf 'podman'
  else
    return 1
  fi
}

# ---------------------------------------------------------------------------
# Pre-flight
# ---------------------------------------------------------------------------
if [[ ! -f "$ENV_EXAMPLE" ]]; then
  printf 'spring.env.example not found: %s\n' "$ENV_EXAMPLE" >&2; exit 1
fi

printf '\n%s\n' "╔══════════════════════════════════════╗"
printf '%s\n'   "║   Spring Voyage — self-host setup   ║"
printf '%s\n\n' "╚══════════════════════════════════════╝"

preflight

if [[ -f "$ENV_FILE" ]]; then
  warn "spring.env already exists."
  OVERWRITE=$(prompt "Overwrite?" "N")
  [[ "${OVERWRITE,,}" == "y" ]] || { printf '\nAborted — existing spring.env unchanged.\n'; exit 0; }
fi

# ---------------------------------------------------------------------------
# 1. Copy example → spring.env
# ---------------------------------------------------------------------------
cp "$ENV_EXAMPLE" "$ENV_FILE"
chmod 600 "$ENV_FILE"
ok "Copied spring.env.example → spring.env (mode 600)"

{
  printf '\n'
  printf '# ---------------------------------------------------------------------------\n'
  printf '# Values written by setup.sh — edit this section to reconfigure.\n'
  printf '# Re-running setup.sh will append a new section; remove the old one.\n'
  printf '# ---------------------------------------------------------------------------\n'
} >> "${ENV_FILE}"

# ---------------------------------------------------------------------------
# 2. Database
# ---------------------------------------------------------------------------
header "Database"
info "PostgreSQL password for the platform's spring user."
info "A random 16-character value is suggested; change it if you prefer."

DEFAULT_PG_PASS=$(gen_hex 8 | head -c 16)
POSTGRES_PASSWORD=$(prompt "POSTGRES_PASSWORD" "$DEFAULT_PG_PASS")
append POSTGRES_PASSWORD "$POSTGRES_PASSWORD"
ok "POSTGRES_PASSWORD set"

# ---------------------------------------------------------------------------
# 3. Secrets encryption key (auto-generated — no prompt)
# ---------------------------------------------------------------------------
AES_KEY=$(python3 -c "import os, base64; print(base64.b64encode(os.urandom(32)).decode())" 2>/dev/null \
          || openssl rand -base64 32)
append SPRING_SECRETS_AES_KEY "$AES_KEY"
ok "SPRING_SECRETS_AES_KEY generated (256-bit AES)"

# ---------------------------------------------------------------------------
# 4. Hostname
# ---------------------------------------------------------------------------
header "Hostname"
info "The public hostname for this deployment."
info "Use 'localhost' for a local-only install, or your FQDN for a public server."

DEPLOY_HOSTNAME=$(prompt "DEPLOY_HOSTNAME" "localhost")
append DEPLOY_HOSTNAME "$DEPLOY_HOSTNAME"
ok "DEPLOY_HOSTNAME=${DEPLOY_HOSTNAME}"

# ---------------------------------------------------------------------------
# 5. GitHub credentials
# ---------------------------------------------------------------------------
header "GitHub App"
info "Every Spring Voyage deployment needs its own GitHub App."
info "The fastest path is the CLI registration wizard, which creates the App"
info "on GitHub and writes all credentials directly into spring.env:"
info ""
info "    spring github-app register"
info ""
info "Run that command after this script finishes and the stack is up."
info "It writes: AppId, AppSlug, PrivateKeyPem, WebhookSecret,"
info "           OAuth__ClientId, and OAuth__ClientSecret."
info ""
info "The one value it does NOT write is the OAuth redirect URI — setup.sh"
info "adds that for you now, derived from the hostname you entered above."
info ""

# OAuth redirect URI (derived from hostname — no prompt needed)
REDIRECT_URI="https://${DEPLOY_HOSTNAME}/api/v1/tenant/connectors/github/oauth/callback"
append GitHub__OAuth__RedirectUri "$REDIRECT_URI"
ok "GitHub__OAuth__RedirectUri=${REDIRECT_URI}"
info ""
info "After running 'spring github-app register', also register this URI"
info "as a Callback URL on your GitHub App's settings page."

# ---------------------------------------------------------------------------
# 6. Dapr sidecar components path (derived — no prompt)
# ---------------------------------------------------------------------------
DAPR_PATH="${REPO_ROOT}/eng/dapr/components/delegated-spring-voyage-agent"
append Dapr__Sidecar__DelegatedSpringVoyageAgentComponentsPath "$DAPR_PATH"
ok "Dapr__Sidecar__DelegatedSpringVoyageAgentComponentsPath set"

# ---------------------------------------------------------------------------
# 7. Agent images
# ---------------------------------------------------------------------------
header "Agent images"
info "Spring Voyage dispatches agent workloads as containers. The platform"
info "needs the agent images in the local Podman image store before you can"
info "validate a unit.  Building locally avoids a GHCR pull."
info ""
info "This takes 5–15 minutes on first run (depends on network / CPU)."
info ""

# Optional step: authenticate to GHCR for pre-built image pulls
if [[ -n "${GHCR_PAT:-}" ]]; then
    if [[ -z "${GHCR_USER:-}" ]]; then
        warn "GHCR_PAT is set but GHCR_USER is not."
        warn "Set GHCR_USER to your GitHub username so GHCR can authenticate the PAT."
        exit 1
    fi

    if _GHCR_CLI=$(resolve_container_cli); then
        info "Logging into GHCR with provided PAT using ${_GHCR_CLI}..."
        if echo "${GHCR_PAT}" | "${_GHCR_CLI}" login ghcr.io -u "${GHCR_USER}" --password-stdin; then
            ok "GHCR login succeeded. Subsequent ${_GHCR_CLI} pull calls will use cached credentials."
        else
            warn "GHCR login failed. Check GHCR_USER and that GHCR_PAT has read:packages scope."
            exit 1
        fi
    else
        warn "GHCR_PAT is set, but neither docker nor podman is available for GHCR login."
        exit 1
    fi
fi

BUILD_IMAGES=$(prompt "Build agent images now? (recommended)" "Y")
if [[ "${BUILD_IMAGES,,}" == "y" ]]; then
    # Resolve container CLI the same way build-agent-images.sh does so the
    # summary message names the right binary.
    if ! _BUILD_CLI=$(resolve_container_cli); then
        warn "Neither docker nor podman found — skipping image build."
        warn "Run 'eng/build/build-agent-images.sh --tag latest' manually before validating units."
        _BUILD_CLI=""
    fi

    if [[ -n "${_BUILD_CLI}" ]]; then
        info "Running: DOCKER=${_BUILD_CLI} eng/build/build-agent-images.sh --tag latest"
        if DOCKER="${_BUILD_CLI}" "${REPO_ROOT}/eng/build/build-agent-images.sh" --tag latest; then
            ok "Agent images built at :latest"
        else
            warn "Image build failed. Run 'eng/build/build-agent-images.sh --tag latest' manually."
        fi
    fi
else
    warn "Skipped.  Run 'eng/build/build-agent-images.sh --tag latest' before validating units."
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
header "Setup complete"
printf '\n'
printf '  spring.env is ready.  Next steps:\n\n'
printf '    1. Build platform images:     ../build/build.sh\n'
printf '    2. Start the stack:           ./deploy.sh up\n'
printf '       (republishes and restarts spring-dispatcher before the worker starts)\n'
printf '    3. Register your GitHub App:  spring github-app register\n'
printf '       (writes GitHub credentials into spring.env and restarts the\n'
printf '        connector — see docs/guide/github-app-setup.md)\n'
printf '\n'
printf '  To reconfigure, edit %s directly\n' "${ENV_FILE}"
printf '  or re-run this script (you will be asked to confirm overwrite).\n'
printf '\n'
