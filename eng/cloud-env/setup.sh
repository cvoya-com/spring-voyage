#!/usr/bin/env bash
# Spring Voyage — Claude Code cloud-environment provisioner.
#
# Replicates the CI toolchain (.NET 10 + Node 20 + Dapr 1.14.1 + ruff) on a
# fresh Claude Code cloud environment so the /build, /test, and /lint commands
# work exactly as they do locally and in .github/workflows/ci.yml.
#
# No Docker/Podman: the .NET test suite uses in-memory EF (not Testcontainers)
# and `dapr init --slim`, so containers are never required for build/test/lint.
# Running the full agent stack (dispatcher + Podman + Postgres) is out of scope.
#
# Cloud-env caching: the setup script runs ONCE, then Anthropic snapshots the
# filesystem and reuses it for later sessions. Keep total runtime under ~5 min
# so the snapshot builds — hence the parallel installs below and no warm build
# by default. Everything written to disk here (incl. ~/.dotnet, ~/.dapr) is
# captured in the snapshot, so later sessions start with the toolchain ready.
#
# Idempotent — safe to re-run. Location-independent — cd's to the repo root.
#
# Usage (paste into the cloud environment's "Setup script" field):
#   bash eng/cloud-env/setup.sh
#
# Environment overrides:
#   CLOUD_ENV_WARM_BUILD - set to any value to also run `dotnet build` (Release)
#                          in setup. Off by default: a full build usually pushes
#                          setup past the ~5-min cache window. The first /build
#                          compiles instead.

# add_to_profile writes lines like `export PATH="$HOME/.dapr/bin:$PATH"` to
# ~/.bashrc verbatim, on purpose: they must expand at shell-startup time, not
# when this script runs. SC2016 (single quotes don't expand) is the intended
# behaviour at every call site below. This directive must precede the first
# command to apply file-wide.
# shellcheck disable=SC2016

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

PROFILE="${HOME}/.bashrc"
# Claude Code's Bash tool starts each command from a fresh login shell, so PATH
# edits must live in the profile to survive — not just in this script's process.
# Only the main shell writes the profile (after the parallel installs join), so
# there is no concurrent-append race.
add_to_profile() { grep -qsF "$1" "${PROFILE}" 2>/dev/null || echo "$1" >>"${PROFILE}"; }

DOTNET_DIR="${HOME}/.dotnet"
DAPR_DIR="${HOME}/.dapr/bin"
NVM_DIR="${HOME}/.nvm"
VENV_DIR="${HOME}/.venv-tools"

# --- Toolchain installers (run concurrently; each installs to a fixed path and
#     does NOT touch the shared profile — PATH wiring happens in the main shell).

install_dotnet() {
  if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    echo "[dotnet] SDK 10 already present"
    return
  fi
  echo "[dotnet] installing SDK 10 (global.json pins 10.0.100)..."
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "${DOTNET_DIR}"
  echo "[dotnet] done"
}

install_node() {
  if command -v node >/dev/null 2>&1 && [ "$(node -p 'process.versions.node.split(".")[0]')" -ge 20 ]; then
    echo "[node] $(node --version) already present (>=20)"
    return
  fi
  echo "[node] installing 20 via nvm (repo requires node>=20, npm>=10)..."
  export NVM_DIR
  [ -s "${NVM_DIR}/nvm.sh" ] || curl -fsSL https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.1/install.sh | bash
  # shellcheck disable=SC1091
  . "${NVM_DIR}/nvm.sh"
  nvm install 20
  nvm alias default 20
  echo "[node] done"
}

install_dapr() {
  if ! command -v dapr >/dev/null 2>&1 && [ ! -x "${DAPR_DIR}/dapr" ]; then
    echo "[dapr] installing CLI 1.14.1..."
    export DAPR_INSTALL_DIR="${DAPR_DIR}"
    mkdir -p "${DAPR_DIR}"
    wget -q https://raw.githubusercontent.com/dapr/cli/master/install/install.sh -O - | /bin/bash -s 1.14.1
  fi
  export PATH="${DAPR_DIR}:${PATH}"
  echo "[dapr] init --slim (no Docker; matches CI)..."
  dapr uninstall --all >/dev/null 2>&1 || true
  dapr init --slim
  echo "[dapr] done"
}

install_ruff() {
  if command -v ruff >/dev/null 2>&1; then
    echo "[ruff] already present"
    return
  fi
  echo "[ruff] installing into isolated venv (avoids PEP 668)..."
  python3 -m venv "${VENV_DIR}"
  "${VENV_DIR}/bin/pip" install -q --upgrade pip ruff
  echo "[ruff] done"
}

echo "==> [1/3] Installing toolchains in parallel (.NET 10, Node 20, Dapr, ruff)"
pids=()
install_dotnet & pids+=($!)
install_node & pids+=($!)
install_dapr & pids+=($!)
install_ruff & pids+=($!)
fail=0
for pid in "${pids[@]}"; do wait "${pid}" || fail=1; done
[ "${fail}" -eq 0 ] || { echo "==> a toolchain install failed (see logs above)"; exit 1; }

echo "==> [2/3] Wiring PATH (profile, single-writer) + sourcing toolchains"
export DOTNET_ROOT="${DOTNET_DIR}"
export PATH="${DOTNET_ROOT}:${DOTNET_ROOT}/tools:${DAPR_DIR}:${PATH}"
add_to_profile 'export DOTNET_ROOT="$HOME/.dotnet"'
add_to_profile 'export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$HOME/.dapr/bin:$PATH"'
if [ -s "${NVM_DIR}/nvm.sh" ]; then
  add_to_profile 'export NVM_DIR="$HOME/.nvm"'
  add_to_profile '[ -s "$NVM_DIR/nvm.sh" ] && . "$NVM_DIR/nvm.sh"'
  export NVM_DIR
  # shellcheck disable=SC1091
  . "${NVM_DIR}/nvm.sh"
fi
if [ -d "${VENV_DIR}" ]; then
  add_to_profile 'export PATH="$HOME/.venv-tools/bin:$PATH"'
  export PATH="${VENV_DIR}/bin:${PATH}"
fi
dotnet --version
node --version
dapr --version
ruff --version

echo "==> [3/3] Restoring in parallel (.NET + local tools | npm workspace)"
fail=0
( dotnet restore SpringVoyage.slnx && dotnet tool restore ) & rpid=$!
npm ci & npid=$!
wait "${rpid}" || fail=1
wait "${npid}" || fail=1
[ "${fail}" -eq 0 ] || { echo "==> a restore step failed (see logs above)"; exit 1; }

if [ -n "${CLOUD_ENV_WARM_BUILD:-}" ]; then
  echo "==> Warm build (CLOUD_ENV_WARM_BUILD set) — may exceed the ~5-min cache window"
  dotnet build SpringVoyage.slnx --no-restore --configuration Release
else
  echo "==> Skipping warm build (default). First /build compiles; keeps setup under the ~5-min cache window."
fi

echo "==> Ready. /build, /test, and /lint now mirror CI."
