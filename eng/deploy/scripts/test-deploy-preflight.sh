#!/usr/bin/env bash
# Unit tests for deploy.sh's `deploy.sh up` guards — the fresh-host checks added
# during the install/deploy audit:
#   preflight_up():
#     - macOS without a running podman machine
#     - a missing locally-built platform image (build.sh / install.sh not run)
#     - a host-published port in use by a foreign holder (abort) vs. one held by
#       this deployment's own service container (skip — issue #2962 regression)
#     - rootless Podman unable to bind privileged Caddy ports (80/443)
#   verify_postgres_password():
#     - a stale spring-postgres-data volume whose password no longer matches
#       spring.env (network scram 28P01 → worker crash-loop)
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

# Consumed by the sourced preflight_up() / verify_postgres_password(); shellcheck
# can't see that through the dynamic `source`, so mark them used (exported) to
# avoid SC2034.
export SPRING_PLATFORM_IMAGE CADDY_HTTP_PORT CADDY_HTTPS_PORT Mcp__Port
export POSTGRES_USER POSTGRES_DB POSTGRES_PASSWORD

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
# Case 5: a host-published port held by a FOREIGN process — no Spring container
# publishes it (make_podman's `ps` output is empty) → die naming the port. The
# #2962 self-holder skip must not weaken this fresh-host conflict guard.
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

# podman stub for the postgres-auth probe: exit ${1} for any `... psql ...` call.
make_podman_pg() {
    cat >"${STUB}/podman" <<PODMAN
#!/usr/bin/env bash
case " \$* " in
  *" psql "*) exit ${1} ;;
esac
exit 0
PODMAN
    chmod +x "${STUB}/podman"
}

# ---------------------------------------------------------------------------
# Case 8: postgres rejects the network password (stale-volume scram mismatch)
# ---------------------------------------------------------------------------
make_uname Linux; make_podman_pg 1     # psql probe → fail (28P01)
POSTGRES_USER="spring"; POSTGRES_DB="spring"; POSTGRES_PASSWORD="some-current-pw"
if ( verify_postgres_password ) >"${TMP_DIR}/out" 2>&1; then
    bad "Case 8: expected postgres-auth failure, got success"; cat "${TMP_DIR}/out"
else
    { grep -q "spring-postgres-data" "${TMP_DIR}/out" && grep -q "POSTGRES_PASSWORD" "${TMP_DIR}/out"; } \
        && ok "Case 8: postgres password mismatch fails fast with wipe guidance" \
        || { bad "Case 8: wrong failure message"; cat "${TMP_DIR}/out"; }
fi

# ---------------------------------------------------------------------------
# Case 9: postgres accepts the network password → verify passes
# ---------------------------------------------------------------------------
make_podman_pg 0       # psql probe → success
if ( verify_postgres_password ) >"${TMP_DIR}/out" 2>&1; then
    ok "Case 9: matching postgres password passes verify"
else
    bad "Case 9: should pass when postgres accepts the password"; cat "${TMP_DIR}/out"
fi

# podman stub whose `rm` exits ${1} (everything else exits 0), for the
# remove_container teardown-tolerance cases.
make_podman_rm() {
    cat >"${STUB}/podman" <<PODMAN
#!/usr/bin/env bash
[[ "\$1" == "rm" ]] && exit ${1}
exit 0
PODMAN
    chmod +x "${STUB}/podman"
}

# ---------------------------------------------------------------------------
# Case 10: `podman rm` exits non-zero (rootless netns cleanup quirk) but the
# container is actually gone → remove_container tolerates it (warns, returns 0)
# so teardown / restart continue instead of aborting under set -e.
# ---------------------------------------------------------------------------
make_podman_rm 1
# container_exists: present on the first check (enter removal), gone afterwards.
_cx=0
container_exists() { _cx=$((_cx + 1)); [[ "${_cx}" -eq 1 ]]; }
if ( remove_container "spring-foo" ) >"${TMP_DIR}/out" 2>&1; then
    grep -qi "continuing" "${TMP_DIR}/out" \
        && ok "Case 10: rm cleanup-error tolerated when the container is actually gone" \
        || { bad "Case 10: tolerated but no warning emitted"; cat "${TMP_DIR}/out"; }
else
    bad "Case 10: remove_container should not fail when the container is gone"; cat "${TMP_DIR}/out"
fi

# ---------------------------------------------------------------------------
# Case 11: `podman rm` fails AND the container persists → genuine failure, die.
# ---------------------------------------------------------------------------
make_podman_rm 1
container_exists() { return 0; }   # always still present
if ( remove_container "spring-foo" ) >"${TMP_DIR}/out" 2>&1; then
    bad "Case 11: remove_container should fail when the container persists"; cat "${TMP_DIR}/out"
else
    grep -qi "failed to remove" "${TMP_DIR}/out" \
        && ok "Case 11: a genuine removal failure still dies" \
        || { bad "Case 11: wrong failure message"; cat "${TMP_DIR}/out"; }
fi

# ---------------------------------------------------------------------------
# Case 12: load_env tolerates the multi-word GitHub App PEM in spring.env.
# `spring github-app register --write-env` writes
#   GitHub__PrivateKeyPem=-----BEGIN RSA PRIVATE KEY-----\n…
# unquoted (correct for podman's literal --env-file). load_env must NOT try to
# `source` that as a command ("RSA: command not found") — GitHub__* are
# container-only, excluded from the shell source, yet still flow to the
# --env-file (RESOLVED) path for the container.
# ---------------------------------------------------------------------------
cat >"${STUB}/envsubst" <<'ENVSUBST'
#!/usr/bin/env bash
exec cat
ENVSUBST
chmod +x "${STUB}/envsubst"
cat >"${ENV_FILE}" <<'PEMENV'
DEPLOY_HOSTNAME=preflight-pem-host
SPRING_SECRETS_AES_KEY=dGVzdGtleTAxMjM0NTY3ODlhYmNkZWZnaGlqa2w=
GitHub__AppId=42
GitHub__PrivateKeyPem=-----BEGIN RSA PRIVATE KEY-----\nAAAABBBBCCCC\n-----END RSA PRIVATE KEY-----
PEMENV
(
    load_env
    printf 'HOST=%s\n' "${DEPLOY_HOSTNAME}"
    printf 'PEM_IN_SHELL=[%s]\n' "${GitHub__PrivateKeyPem:-}"
    grep -q '^GitHub__PrivateKeyPem=' "${RESOLVED_ENV_FILE}" && printf 'RESOLVED_HAS_PEM=yes\n'
) >"${TMP_DIR}/out" 2>&1
if grep -qi "command not found" "${TMP_DIR}/out"; then
    bad "Case 12: load_env executed PEM fragments (\"command not found\")"; cat "${TMP_DIR}/out"
elif grep -q '^HOST=preflight-pem-host$' "${TMP_DIR}/out" \
     && grep -q '^PEM_IN_SHELL=\[\]$' "${TMP_DIR}/out" \
     && grep -q '^RESOLVED_HAS_PEM=yes$' "${TMP_DIR}/out"; then
    ok "Case 12: load_env skips the multi-word GitHub__ PEM (kept for --env-file)"
else
    bad "Case 12: load_env did not handle the GitHub__ PEM as expected"; cat "${TMP_DIR}/out"
fi

# podman stub for the self-vs-foreign port-holder check (#2962): image present,
# machine running, and `podman ps` prints the lines staged in ${PS_FILE}
# (Names|Ports) so spring_container_publishing_port() can classify the holder.
PS_FILE="${TMP_DIR}/ps_output"
make_podman_ps() {
    cat >"${STUB}/podman" <<PODMAN
#!/usr/bin/env bash
if [[ "\$1 \$2" == "image exists" ]]; then exit 0; fi
if [[ "\$1 \$2" == "machine list" ]]; then echo "true"; exit 0; fi
if [[ "\$1" == "ps" ]]; then cat "${PS_FILE}" 2>/dev/null; exit 0; fi
exit 0
PODMAN
    chmod +x "${STUB}/podman"
}

# ---------------------------------------------------------------------------
# Case 13: re-`up` over this deployment's own live stack — the busy host port is
# published by our own spring-caddy → skip it and pass (issue #2962). Before the
# fix, #2930's guard `die`d here on the deployment's own already-running caddy.
# ---------------------------------------------------------------------------
make_uname Linux; make_podman_ps
unprivileged_port_floor() { echo 80; }
SPRING_PLATFORM_IMAGE="localhost/spring-voyage:latest"
CADDY_HTTP_PORT="8080"; CADDY_HTTPS_PORT="8443"; Mcp__Port="5050"
port_in_use() { [[ "$1" == "8080" ]] && return 0 || return 1; }   # our Caddy HTTP
printf 'spring-caddy|0.0.0.0:8080->80/tcp, 0.0.0.0:8443->443/tcp\n' >"${PS_FILE}"
if run_preflight; then
    grep -q "published by this deployment's own 'spring-caddy'" "${TMP_DIR}/out" \
        && ok "Case 13: a port held by our own spring-caddy is skipped, not a conflict" \
        || { bad "Case 13: passed but without the self-holder log line"; cat "${TMP_DIR}/out"; }
else
    bad "Case 13: re-up over our own live stack should pass preflight"; cat "${TMP_DIR}/out"
fi

# ---------------------------------------------------------------------------
# Case 14: the busy host port is published by a FOREIGN container (not spring-*)
# → still abort. The self-holder skip is scoped to our own containers, so the
# #2930 fresh-host guard is preserved against any other holder.
# ---------------------------------------------------------------------------
make_uname Linux; make_podman_ps
unprivileged_port_floor() { echo 80; }
SPRING_PLATFORM_IMAGE="localhost/spring-voyage:latest"
CADDY_HTTP_PORT="8080"; CADDY_HTTPS_PORT="8443"; Mcp__Port="5050"
port_in_use() { [[ "$1" == "5050" ]] && return 0 || return 1; }   # MCP busy
printf 'some-other-tool|0.0.0.0:5050->5050/tcp\n' >"${PS_FILE}"   # foreign, not spring-*
if run_preflight; then
    bad "Case 14: a foreign container holding the port must still abort"; cat "${TMP_DIR}/out"
else
    { grep -q "already in use" "${TMP_DIR}/out" && grep -q "5050" "${TMP_DIR}/out"; } \
        && ok "Case 14: a non-spring container on the port still fails (guard preserved)" \
        || { bad "Case 14: wrong failure message"; cat "${TMP_DIR}/out"; }
fi

printf '\n  passed: %d\n  failed: %d\n' "${PASS}" "${FAIL}"
[[ "${FAIL}" -eq 0 ]]
