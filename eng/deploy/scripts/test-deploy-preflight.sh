#!/usr/bin/env bash
# Unit tests for deploy.sh preflight_up() — the fresh-host guards added during
# the #2925 install/deploy audit:
#   - macOS without a running podman machine
#   - a missing locally-built platform image (build.sh / install.sh not run)
#   - a host-published port already in use
#   - rootless Podman unable to bind privileged Caddy ports (80/443)
#
# Strategy mirrors test-deploy-dispatcher-rebuild.sh: source deploy.sh's
# function definitions (minus its main dispatch), then stub `uname` / `podman`
# / `id` on PATH and override the pure helpers (`port_in_use`,
# `unprivileged_port_floor`) per case so the checks are deterministic on any OS.
#
# Exit 0 on success, non-zero on the first failed assertion.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOYMENT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

TMP_DIR="$(mktemp -d -t spring-deploy-preflight.XXXXXX)"
trap 'rm -rf "${TMP_DIR}"' EXIT

DEPLOY_LIB="${TMP_DIR}/deploy-lib.sh"
# Load deploy.sh's function definitions without running its main dispatch.
sed '/^main "\$@"/d' "${DEPLOYMENT_DIR}/deploy.sh" >"${DEPLOY_LIB}"
# shellcheck disable=SC1090
source "${DEPLOY_LIB}"

# Consumed by the sourced preflight_up(); shellcheck can't see that through the
# dynamic `source`, so mark them as used (exported) to avoid SC2034.
export SPRING_PLATFORM_IMAGE CADDY_HTTP_PORT CADDY_HTTPS_PORT Mcp__Port

PASS=0
FAIL=0
ok()  { printf '  [ ok ] %s\n' "$*"; PASS=$((PASS + 1)); }
bad() { printf '  [FAIL] %s\n' "$*"; FAIL=$((FAIL + 1)); }

STUB="${TMP_DIR}/bin"
mkdir -p "${STUB}"
export PATH="${STUB}:${PATH}"

# Force the OS branch.
make_uname() {
    cat >"${STUB}/uname" <<UNAME
#!/usr/bin/env bash
echo "$1"
UNAME
    chmod +x "${STUB}/uname"
}

# \$1 = exit code for 'podman image exists' (0 present / 1 missing)
# \$2 = 'podman machine list' output (true / false)
make_podman() {
    cat >"${STUB}/podman" <<PODMAN
#!/usr/bin/env bash
if [[ "\$1 \$2" == "image exists" ]]; then exit ${1}; fi
if [[ "\$1 \$2" == "machine list" ]]; then echo "${2}"; exit 0; fi
exit 0
PODMAN
    chmod +x "${STUB}/podman"
}

# Force a non-root uid so the rootless branch engages regardless of test runner.
cat >"${STUB}/id" <<'IDSTUB'
#!/usr/bin/env bash
[[ "$1" == "-u" ]] && { echo "1000"; exit 0; }
exec /usr/bin/id "$@"
IDSTUB
chmod +x "${STUB}/id"

# deploy.sh globals referenced by preflight_up / its die messages.
ENV_FILE="${TMP_DIR}/spring.env"
: >"${ENV_FILE}"

# Run preflight_up in a subshell so a `die` (exit 1) is captured, not fatal here.
run_preflight() { ( preflight_up ) >"${TMP_DIR}/out" 2>&1; }

# ---------------------------------------------------------------------------
# Case 1: Linux rootless, default 80/443, floor 1024 → privileged-port die
# ---------------------------------------------------------------------------
make_uname Linux; make_podman 0 true
port_in_use() { return 1; }                 # every port free
unprivileged_port_floor() { echo 1024; }
SPRING_PLATFORM_IMAGE="localhost/spring-voyage:latest"
CADDY_HTTP_PORT=""; CADDY_HTTPS_PORT=""; Mcp__Port=""
if run_preflight; then
    bad "Case 1: expected privileged-port failure, got success"
    cat "${TMP_DIR}/out"
else
    grep -q "ip_unprivileged_port_start" "${TMP_DIR}/out" \
        && ok "Case 1: rootless 80/443 fails with floor guidance" \
        || { bad "Case 1: wrong failure message"; cat "${TMP_DIR}/out"; }
fi

# ---------------------------------------------------------------------------
# Case 2: Linux rootless, preset high ports ≥ floor → passes
# ---------------------------------------------------------------------------
make_uname Linux; make_podman 0 true
port_in_use() { return 1; }
unprivileged_port_floor() { echo 1024; }
SPRING_PLATFORM_IMAGE="localhost/spring-voyage:latest"
CADDY_HTTP_PORT="8080"; CADDY_HTTPS_PORT="8443"; Mcp__Port="5050"
if run_preflight; then
    ok "Case 2: high ports ≥ floor pass preflight"
else
    bad "Case 2: high ports should pass"; cat "${TMP_DIR}/out"
fi

# ---------------------------------------------------------------------------
# Case 3: missing localhost/ platform image → die with build hint
# ---------------------------------------------------------------------------
make_uname Linux; make_podman 1 true     # image exists → exit 1 (missing)
port_in_use() { return 1; }
unprivileged_port_floor() { echo 80; }   # floor low so we reach the image check cleanly
SPRING_PLATFORM_IMAGE="localhost/spring-voyage:latest"
CADDY_HTTP_PORT="8080"; CADDY_HTTPS_PORT="8443"; Mcp__Port="5050"
if run_preflight; then
    bad "Case 3: expected missing-image failure, got success"; cat "${TMP_DIR}/out"
else
    grep -q "not in the local store" "${TMP_DIR}/out" \
        && ok "Case 3: missing localhost image fails with build hint" \
        || { bad "Case 3: wrong failure message"; cat "${TMP_DIR}/out"; }
fi

# ---------------------------------------------------------------------------
# Case 4: registry (ghcr.io) image absent locally → NOT a failure (podman pulls)
# ---------------------------------------------------------------------------
make_uname Linux; make_podman 1 true     # image exists → missing
port_in_use() { return 1; }
unprivileged_port_floor() { echo 80; }
SPRING_PLATFORM_IMAGE="ghcr.io/cvoya-com/spring-voyage:1.0.0"
CADDY_HTTP_PORT="8080"; CADDY_HTTPS_PORT="8443"; Mcp__Port="5050"
if run_preflight; then
    ok "Case 4: absent ghcr.io ref is left for podman to pull (no die)"
else
    bad "Case 4: ghcr.io ref should not trip the local-image guard"; cat "${TMP_DIR}/out"
fi

# ---------------------------------------------------------------------------
# Case 5: a host-published port already in use → die naming the port
# ---------------------------------------------------------------------------
make_uname Linux; make_podman 0 true
unprivileged_port_floor() { echo 80; }
SPRING_PLATFORM_IMAGE="localhost/spring-voyage:latest"
CADDY_HTTP_PORT="8080"; CADDY_HTTPS_PORT="8443"; Mcp__Port="5050"
port_in_use() { [[ "$1" == "5050" ]] && return 0 || return 1; }   # MCP busy
if run_preflight; then
    bad "Case 5: expected port-in-use failure, got success"; cat "${TMP_DIR}/out"
else
    { grep -q "already in use" "${TMP_DIR}/out" && grep -q "5050" "${TMP_DIR}/out"; } \
        && ok "Case 5: occupied MCP port fails naming the port" \
        || { bad "Case 5: wrong failure message"; cat "${TMP_DIR}/out"; }
fi

# ---------------------------------------------------------------------------
# Case 6: macOS without a running podman machine → die
# ---------------------------------------------------------------------------
make_uname Darwin; make_podman 0 false    # machine list → "false"
port_in_use() { return 1; }
unprivileged_port_floor() { echo 1024; }
SPRING_PLATFORM_IMAGE="localhost/spring-voyage:latest"
CADDY_HTTP_PORT=""; CADDY_HTTPS_PORT=""; Mcp__Port=""
if run_preflight; then
    bad "Case 6: expected podman-machine failure, got success"; cat "${TMP_DIR}/out"
else
    grep -q "Podman machine" "${TMP_DIR}/out" \
        && ok "Case 6: macOS without a running machine fails fast" \
        || { bad "Case 6: wrong failure message"; cat "${TMP_DIR}/out"; }
fi

# ---------------------------------------------------------------------------
# Case 7: macOS with a running machine, image present, ports free → passes
# (Darwin skips the Linux rootless floor check entirely.)
# ---------------------------------------------------------------------------
make_uname Darwin; make_podman 0 true
port_in_use() { return 1; }
unprivileged_port_floor() { echo 1024; }
SPRING_PLATFORM_IMAGE="localhost/spring-voyage:latest"
CADDY_HTTP_PORT=""; CADDY_HTTPS_PORT=""; Mcp__Port=""
if run_preflight; then
    ok "Case 7: macOS with running machine + image + free ports passes"
else
    bad "Case 7: should pass on a healthy macOS host"; cat "${TMP_DIR}/out"
fi

printf '\n  passed: %d\n  failed: %d\n' "${PASS}" "${FAIL}"
[[ "${FAIL}" -eq 0 ]]
