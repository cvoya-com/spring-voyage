#!/usr/bin/env bash
# Dry-run / fixture-driven unit tests for eng/install/install.sh.
#
# Strategy: run install.sh against a stubbed GitHub release served from a
# local directory. We:
#   1. Stage a fake bundle/dispatcher/CLI under a fixture dir.
#   2. Compute real SHA256SUMS over those fixtures.
#   3. Shadow `curl` and `podman` via a PATH-injected stub directory so the
#      installer believes it's downloading + pulling without touching the
#      network.
#   4. Override $HOME so all install artefacts land in a temp dir.
#
# Cases covered:
#   1. Re-run refusal: install.sh into an existing install root fails fast.
#   2. RID detection across linux-x64, linux-arm64, osx-x64, osx-arm64
#      (by overriding `uname` via the stub PATH).
#   3. Generated spring.env contains the expected SPRING_PLATFORM_IMAGE,
#      install paths, and is mode 0600.
#   4. Symlinks point at the right targets.
#   5. uninstall.sh default preserves spring.env + host/ + workspaces/.
#   6. uninstall.sh --purge removes them.
#   7. uninstall.sh is idempotent (second run is a no-op).
#
# Exit 0 on success, non-zero on the first failed assertion.

set -euo pipefail

# Note: when running this script inside the worktree, BASH_SOURCE may
# be a symlink. resolve via `cd ... && pwd` so the path is canonical.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
INSTALL_SH="${INSTALL_DIR}/install.sh"
UNINSTALL_SH="${INSTALL_DIR}/uninstall.sh"
WRAPPER_SH="${INSTALL_DIR}/voyage"

[[ -x "${INSTALL_SH}" ]] || { echo "install.sh not executable: ${INSTALL_SH}" >&2; exit 1; }
[[ -x "${UNINSTALL_SH}" ]] || { echo "uninstall.sh not executable: ${UNINSTALL_SH}" >&2; exit 1; }
[[ -x "${WRAPPER_SH}" ]] || { echo "voyage wrapper not executable: ${WRAPPER_SH}" >&2; exit 1; }

FIXTURE_VERSION="0.0.0-test"
# Post-#2229 canonical tag form: spring-voyage-v<semver>. The installer's
# resolve_latest_release() reads this tag_name from the curl stub's
# `api.github.com/repos/.../releases/latest` response, so it must match
# the real GitHub Release tag shape.
FIXTURE_TAG="spring-voyage-v${FIXTURE_VERSION}"
PLATFORM_IMAGE="ghcr.io/cvoya-com/spring-voyage:${FIXTURE_VERSION}"

TMP_BASE="$(mktemp -d -t spring-voyage-install-tests.XXXXXX)"
trap 'rm -rf "${TMP_BASE}"' EXIT

PASS=0
FAIL=0

ok()   { printf '  \033[0;32m✓\033[0m  %s\n' "$*"; PASS=$((PASS+1)); }
bad()  { printf '  \033[0;31m✗\033[0m  %s\n' "$*"; FAIL=$((FAIL+1)); }
note() { printf '      %s\n' "$*"; }
hdr()  { printf '\n\033[1;36m== %s ==\033[0m\n' "$*"; }

assert_eq() {
  local actual="$1" expected="$2" msg="$3"
  if [[ "$actual" == "$expected" ]]; then
    ok "$msg"
  else
    bad "$msg (expected '$expected', got '$actual')"
  fi
}

assert_file_mode() {
  local file="$1" expected="$2" msg="$3"
  local mode
  if [[ "$(uname -s)" == "Darwin" ]]; then
    mode="$(stat -f '%Lp' "$file" 2>/dev/null || echo "")"
  else
    mode="$(stat -c '%a' "$file" 2>/dev/null || echo "")"
  fi
  assert_eq "$mode" "$expected" "$msg"
}

assert_contains() {
  local file="$1" pattern="$2" msg="$3"
  if grep -qE "$pattern" "$file" 2>/dev/null; then
    ok "$msg"
  else
    bad "$msg (pattern '$pattern' not found in ${file})"
  fi
}

assert_path_exists() {
  local path="$1" msg="$2"
  if [[ -e "$path" || -L "$path" ]]; then ok "$msg"; else bad "$msg ($path missing)"; fi
}

assert_path_absent() {
  local path="$1" msg="$2"
  if [[ ! -e "$path" && ! -L "$path" ]]; then ok "$msg"; else bad "$msg ($path still present)"; fi
}

# ---------------------------------------------------------------------------
# Build a fake release in $TMP_BASE/release/
# ---------------------------------------------------------------------------
build_fixture_release() {
  local rel="${TMP_BASE}/release"
  rm -rf "${rel}"; mkdir -p "${rel}"

  # Bundle staging.
  local stage="${TMP_BASE}/stage/bundle"
  rm -rf "${TMP_BASE}/stage"; mkdir -p "${stage}/scripts" "${stage}/dapr/components/delegated-spring-voyage-agent"

  # Minimal deploy.sh stub that records its invocation and exits 0.
  cat > "${stage}/deploy.sh" <<'DEPLOY'
#!/usr/bin/env bash
# Fixture deploy.sh — records invocations for the install test.
printf '%s\n' "deploy.sh $*" >> "${SPRING_ENV_FILE%/spring.env}/deploy.log"
exit 0
DEPLOY
  chmod +x "${stage}/deploy.sh"

  # spring.env.example — minimal, enough to be present.
  cat > "${stage}/spring.env.example" <<'EOFENV'
# Fixture spring.env.example
DEPLOY_HOSTNAME=localhost
POSTGRES_PASSWORD=change-me
SPRING_SECRETS_AES_KEY=REPLACE_ME
SPRING_PLATFORM_IMAGE=localhost/spring-voyage:latest
SPRING_IMAGE_TAG=latest
EOFENV

  # Fixture uninstall.sh — copied from the real one so the bundle-side
  # uninstall semantics are exactly what the installer will invoke. Real
  # release pipelines copy uninstall.sh into the bundle from eng/install/.
  cp "${UNINSTALL_SH}" "${stage}/uninstall.sh"
  chmod +x "${stage}/uninstall.sh"

  # Fixture voyage wrapper — same rationale as uninstall.sh above.
  # The real release-pipeline copies eng/install/voyage into the
  # bundle so install.sh can `cp` it to ~/.local/bin/.
  cp "${WRAPPER_SH}" "${stage}/voyage"
  chmod +x "${stage}/voyage"

  # Dapr components placeholder.
  echo '# fixture' > "${stage}/dapr/components/delegated-spring-voyage-agent/README.md"

  # manifest.json — schema v2 (see #2229: dispatcher_version and
  # cli_version were always equal to `version` and have been removed).
  cat > "${stage}/manifest.json" <<EOFMANIFEST
{
  "bundle_schema_version": 2,
  "version": "${FIXTURE_VERSION}",
  "platform_image": "${PLATFORM_IMAGE}"
}
EOFMANIFEST

  # Dispatcher staging — populated as a sibling subdirectory under the
  # unified per-RID stage tree below.
  local disp_payload="${TMP_BASE}/payload-dispatcher"
  rm -rf "${disp_payload}"; mkdir -p "${disp_payload}"
  cat > "${disp_payload}/Cvoya.Spring.Dispatcher" <<'DISPATCHER'
#!/usr/bin/env bash
echo "fixture dispatcher: $*"
DISPATCHER
  chmod +x "${disp_payload}/Cvoya.Spring.Dispatcher"

  # CLI staging — populated as a sibling subdirectory under the unified
  # per-RID stage tree below.
  local cli_payload="${TMP_BASE}/payload-cli"
  rm -rf "${cli_payload}"; mkdir -p "${cli_payload}"
  cat > "${cli_payload}/spring" <<'CLI'
#!/usr/bin/env bash
# Fixture spring CLI — supports `spring github-app register --name ... --env-path ... --write-env`
# Records the invocation and exits 0 unless ${SPRING_FIXTURE_GH_REGISTER_FAIL} is set.
printf '%s\n' "spring $*" >> "${SPRING_FIXTURE_CLI_LOG:-/dev/null}"
if [[ -n "${SPRING_FIXTURE_GH_REGISTER_FAIL:-}" ]]; then exit 1; fi
exit 0
CLI
  chmod +x "${cli_payload}/spring"

  # Per-RID unified host archive (#2243). Each archive contains the
  # bundle/, cli/, and dispatcher/ subtrees that install.sh expects to
  # find at RELEASE_DIR/{bundle,cli,dispatcher} after extraction. The
  # same fixture binaries are reused across RIDs; the installer never
  # executes them in dry-run, it only checks they exist after extraction.
  local rids=(linux-x64 linux-arm64 osx-x64 osx-arm64)
  for rid in "${rids[@]}"; do
    local rid_stage="${TMP_BASE}/stage-${rid}"
    rm -rf "${rid_stage}"
    mkdir -p "${rid_stage}/cli" "${rid_stage}/dispatcher"
    cp -R "${stage}" "${rid_stage}/bundle"
    cp "${cli_payload}/spring"                 "${rid_stage}/cli/spring"
    cp "${disp_payload}/Cvoya.Spring.Dispatcher" "${rid_stage}/dispatcher/Cvoya.Spring.Dispatcher"
    chmod +x "${rid_stage}/cli/spring" "${rid_stage}/dispatcher/Cvoya.Spring.Dispatcher"
    tar -C "${rid_stage}" -czf "${rel}/spring-voyage-${FIXTURE_VERSION}-${rid}.tar.gz" .
  done

  # SHA256SUMS — real checksums computed over the staged archives so the
  # installer's verification step actually validates them.
  ( cd "${rel}" && \
      ( command -v sha256sum >/dev/null && sha256sum -- * || shasum -a 256 -- * ) \
      | sort > SHA256SUMS )
}

# ---------------------------------------------------------------------------
# Stub directory: shadows `curl`, `podman`, and optionally `uname`.
# ---------------------------------------------------------------------------
make_stub_path() {
  local stub_dir="$1" fake_uname_s="${2:-}" fake_uname_m="${3:-}"
  rm -rf "${stub_dir}"; mkdir -p "${stub_dir}"

  # curl stub — serves files from $FIXTURE_RELEASE_DIR by mapping the
  # standard release URL prefix to a local path.
  cat > "${stub_dir}/curl" <<CURL
#!/usr/bin/env bash
# Stub curl: maps github.com/<owner>/<repo>/releases/download/<tag>/<file>
# to \${FIXTURE_RELEASE_DIR}/<file>. Supports the flags the installer uses.
set -e
OUT=""
URL=""
while [[ \$# -gt 0 ]]; do
  case "\$1" in
    -o) OUT="\$2"; shift 2 ;;
    -o*) OUT="\${1#-o}"; shift ;;
    -f|-s|-S|-L|-fSL|-fsSL|-fSLI) shift ;;
    --retry|--retry-delay|--retry-max-time) shift 2 ;;
    --) shift ;;
    http*|https*) URL="\$1"; shift ;;
    *) shift ;;
  esac
done
if [[ -z "\$URL" ]]; then echo "stub-curl: no URL" >&2; exit 22; fi

# Latest-release JSON probe.
case "\$URL" in
  *api.github.com/repos/*/releases/latest*)
    printf '{"tag_name":"%s"}\n' "${FIXTURE_TAG}"
    exit 0 ;;
esac

filename="\${URL##*/}"
src="\${FIXTURE_RELEASE_DIR}/\${filename}"
if [[ ! -f "\$src" ]]; then
  echo "stub-curl: missing fixture file \$src (for URL \$URL)" >&2
  exit 22
fi
if [[ -n "\$OUT" ]]; then cp "\$src" "\$OUT"; else cat "\$src"; fi
CURL
  chmod +x "${stub_dir}/curl"

  # podman stub — records calls, succeeds.
  cat > "${stub_dir}/podman" <<'PODMAN'
#!/usr/bin/env bash
case "$1" in
  version)
    case "$2" in
      --format) echo "4.9.0" ;;
      *) echo "Version: 4.9.0" ;;
    esac ;;
  machine)
    case "$2" in
      list) echo "true" ;;
      *) echo "ok" ;;
    esac ;;
  pull) printf '%s\n' "podman pull $2" >> "${SPRING_FIXTURE_PODMAN_LOG:-/dev/null}" ;;
  *) printf '%s\n' "podman $*" >> "${SPRING_FIXTURE_PODMAN_LOG:-/dev/null}" ;;
esac
exit 0
PODMAN
  chmod +x "${stub_dir}/podman"

  # envsubst stub — install.sh's pre-flight only checks presence (the bundle's
  # deploy.sh, which actually uses envsubst, is stubbed in these tests). cat
  # passthrough keeps it harmless if ever invoked.
  cat > "${stub_dir}/envsubst" <<'ENVSUBST'
#!/usr/bin/env bash
cat
ENVSUBST
  chmod +x "${stub_dir}/envsubst"

  # Optional uname override for RID detection tests.
  if [[ -n "$fake_uname_s" ]]; then
    cat > "${stub_dir}/uname" <<UNAME
#!/usr/bin/env bash
case "\$1" in
  -s) echo "${fake_uname_s}" ;;
  -m) echo "${fake_uname_m}" ;;
  *)  echo "${fake_uname_s}" ;;
esac
UNAME
    chmod +x "${stub_dir}/uname"
  fi
}

# ---------------------------------------------------------------------------
# Test runners
# ---------------------------------------------------------------------------
run_install() {
  local home_dir="$1"; shift
  local stub_dir="$1"; shift
  HOME="${home_dir}" \
    SPRING_VOYAGE_VERSION="${FIXTURE_VERSION}" \
    FIXTURE_RELEASE_DIR="${TMP_BASE}/release" \
    SPRING_FIXTURE_PODMAN_LOG="${home_dir}/.spring-voyage/podman.log" \
    SPRING_FIXTURE_CLI_LOG="${home_dir}/.spring-voyage/cli.log" \
    SPRING_INSTALL_SKIP_PORT_CHECK=1 \
    PATH="${stub_dir}:${PATH}" \
    bash "${INSTALL_SH}" --yes "$@"
}

run_uninstall() {
  local home_dir="$1"; shift
  HOME="${home_dir}" \
    bash "${UNINSTALL_SH}" "$@"
}

# Variant of run_install that DOES NOT pass SPRING_INSTALL_SKIP_PORT_CHECK so
# port-conflict tests can exercise the real port-availability path. The caller
# may pass SPRING_DISPATCHER_PORT to point the dispatcher check at a port the
# test fixture controls, Mcp__Port / CADDY_HTTP_PORT / CADDY_HTTPS_PORT to
# preset host ports, and SPRING_INSTALL_UNPRIV_PORT_START to pin the kernel's
# unprivileged-port floor for the rootless privileged-port check (Cases 13-15).
run_install_with_port_check() {
  local home_dir="$1"; shift
  local stub_dir="$1"; shift
  HOME="${home_dir}" \
    SPRING_VOYAGE_VERSION="${FIXTURE_VERSION}" \
    FIXTURE_RELEASE_DIR="${TMP_BASE}/release" \
    SPRING_FIXTURE_PODMAN_LOG="${home_dir}/.spring-voyage/podman.log" \
    SPRING_FIXTURE_CLI_LOG="${home_dir}/.spring-voyage/cli.log" \
    SPRING_DISPATCHER_PORT="${SPRING_DISPATCHER_PORT:-}" \
    Mcp__Port="${Mcp__Port:-}" \
    CADDY_HTTP_PORT="${CADDY_HTTP_PORT:-}" \
    CADDY_HTTPS_PORT="${CADDY_HTTPS_PORT:-}" \
    SPRING_INSTALL_UNPRIV_PORT_START="${SPRING_INSTALL_UNPRIV_PORT_START:-}" \
    PATH="${stub_dir}:${PATH}" \
    bash "${INSTALL_SH}" --yes "$@"
}

# ---------------------------------------------------------------------------
# Build fixtures once.
# ---------------------------------------------------------------------------
hdr "Building fixture release"
build_fixture_release
note "Fixture release at: ${TMP_BASE}/release"

# ===========================================================================
# Case 1: Happy-path install, RID linux-x64
# ===========================================================================
hdr "Case 1 — happy-path install (linux-x64)"
HOME_DIR_1="${TMP_BASE}/home-1"
STUB_DIR_1="${TMP_BASE}/stub-1"
mkdir -p "${HOME_DIR_1}"
make_stub_path "${STUB_DIR_1}" "Linux" "x86_64"

if run_install "${HOME_DIR_1}" "${STUB_DIR_1}" --no-start >"${TMP_BASE}/run1.out" 2>&1; then
  ok "install.sh --yes --no-start exit 0"
else
  bad "install.sh exited non-zero"
  cat "${TMP_BASE}/run1.out" >&2
fi

INSTALL_ROOT_1="${HOME_DIR_1}/.spring-voyage"
ENV_FILE_1="${INSTALL_ROOT_1}/spring.env"
assert_path_exists "${INSTALL_ROOT_1}/current" "current symlink exists"
assert_path_exists "${INSTALL_ROOT_1}/releases/${FIXTURE_VERSION}/bundle/deploy.sh" "bundle deploy.sh extracted"
assert_path_exists "${HOME_DIR_1}/.local/bin/spring" "spring CLI symlink exists"
assert_path_exists "${HOME_DIR_1}/.local/bin/voyage" "voyage wrapper exists"
assert_path_exists "${ENV_FILE_1}" "spring.env generated"
assert_file_mode  "${ENV_FILE_1}" "600" "spring.env mode is 0600"
assert_contains   "${ENV_FILE_1}" "^SPRING_PLATFORM_IMAGE=${PLATFORM_IMAGE//\//\\/}$" "spring.env pins SPRING_PLATFORM_IMAGE"
assert_contains   "${ENV_FILE_1}" "^SPRING_IMAGE_TAG=${FIXTURE_VERSION}$" "spring.env pins SPRING_IMAGE_TAG"
assert_contains   "${ENV_FILE_1}" "^DEPLOY_HOSTNAME=localhost$" "DEPLOY_HOSTNAME=localhost (default)"
assert_contains   "${ENV_FILE_1}" "^GitHub__OAuth__RedirectUri=https://localhost/" "OAuth redirect URI derived"
assert_contains   "${ENV_FILE_1}" "^Dapr__Sidecar__DelegatedSpringVoyageAgentComponentsPath=${INSTALL_ROOT_1//\//\\/}/current/dapr/components/delegated-spring-voyage-agent$" "Dapr components path absolute"
assert_contains   "${ENV_FILE_1}" "^SPRING_DISPATCHER_BIN=${INSTALL_ROOT_1//\//\\/}/releases/${FIXTURE_VERSION}/dispatcher/" "SPRING_DISPATCHER_BIN points at release dir"

# ===========================================================================
# Case 2: Re-run refusal
# ===========================================================================
hdr "Case 2 — re-run refusal"
if run_install "${HOME_DIR_1}" "${STUB_DIR_1}" --no-start >"${TMP_BASE}/run2.out" 2>&1; then
  bad "install.sh succeeded on second run; expected refusal"
else
  ok "install.sh refused re-run with non-zero exit"
fi
if grep -q "already installed" "${TMP_BASE}/run2.out"; then
  ok "re-run output mentions 'already installed'"
else
  bad "re-run output missing 'already installed'"
  cat "${TMP_BASE}/run2.out" >&2
fi

# ===========================================================================
# Case 3: --force bypasses the refusal
# ===========================================================================
hdr "Case 3 — --force bypasses already-installed"
if run_install "${HOME_DIR_1}" "${STUB_DIR_1}" --no-start --force >"${TMP_BASE}/run3.out" 2>&1; then
  ok "install.sh --force succeeded"
else
  bad "install.sh --force failed"
  cat "${TMP_BASE}/run3.out" >&2
fi

# ===========================================================================
# Case 4: RID detection across POSIX RIDs
# ===========================================================================
hdr "Case 4 — RID detection on linux-arm64, osx-x64, osx-arm64"
case4_rids=(
  "Linux:aarch64:linux-arm64"
  "Darwin:x86_64:osx-x64"
  "Darwin:arm64:osx-arm64"
)
for triplet in "${case4_rids[@]}"; do
  IFS=":" read -r uname_s uname_m expected_rid <<<"$triplet"
  HOME_DIR_N="${TMP_BASE}/home-${expected_rid}"
  STUB_DIR_N="${TMP_BASE}/stub-${expected_rid}"
  mkdir -p "${HOME_DIR_N}"
  make_stub_path "${STUB_DIR_N}" "${uname_s}" "${uname_m}"
  # podman machine list expects Darwin to look running too — stub
  # already returns true.
  if run_install "${HOME_DIR_N}" "${STUB_DIR_N}" --no-start >"${TMP_BASE}/run4-${expected_rid}.out" 2>&1; then
    if grep -q "RID detected: ${expected_rid}" "${TMP_BASE}/run4-${expected_rid}.out"; then
      ok "RID detected as ${expected_rid}"
    else
      bad "RID line for ${expected_rid} not found"
      cat "${TMP_BASE}/run4-${expected_rid}.out" >&2
    fi
  else
    bad "install.sh failed for RID ${expected_rid}"
    tail -40 "${TMP_BASE}/run4-${expected_rid}.out" >&2
  fi
done

# ===========================================================================
# Case 5: uninstall default preserves operator data
# ===========================================================================
hdr "Case 5 — uninstall default preserves spring.env + host/ + workspaces/"
# Ensure ~/.spring-voyage/host and ~/.spring-voyage/workspaces exist for the preserve test.
mkdir -p "${INSTALL_ROOT_1}/host" "${INSTALL_ROOT_1}/workspaces"
echo "fixture" > "${INSTALL_ROOT_1}/host/dispatcher.env"
echo "fixture" > "${INSTALL_ROOT_1}/workspaces/example.txt"

if run_uninstall "${HOME_DIR_1}" --yes --force >"${TMP_BASE}/uninstall1.out" 2>&1; then
  ok "uninstall default exit 0"
else
  bad "uninstall default failed"
  cat "${TMP_BASE}/uninstall1.out" >&2
fi
assert_path_absent "${INSTALL_ROOT_1}/releases" "releases/ removed"
assert_path_absent "${INSTALL_ROOT_1}/current"  "current symlink removed"
assert_path_absent "${HOME_DIR_1}/.local/bin/spring" "spring symlink removed"
assert_path_absent "${HOME_DIR_1}/.local/bin/voyage" "voyage wrapper removed"
assert_path_exists "${ENV_FILE_1}" "spring.env preserved by default"
assert_path_exists "${INSTALL_ROOT_1}/host" "host/ preserved by default"
assert_path_exists "${INSTALL_ROOT_1}/workspaces" "workspaces/ preserved by default"

# ===========================================================================
# Case 6: uninstall --purge removes operator data
# ===========================================================================
hdr "Case 6 — uninstall --purge removes spring.env + host/ + workspaces/"
# Re-install first so there's a current install to purge.
mkdir -p "${HOME_DIR_1}"
if run_install "${HOME_DIR_1}" "${STUB_DIR_1}" --no-start --force >"${TMP_BASE}/run6.out" 2>&1; then
  ok "re-install for purge test exit 0"
else
  bad "re-install for purge test failed"
  cat "${TMP_BASE}/run6.out" >&2
fi
mkdir -p "${INSTALL_ROOT_1}/host" "${INSTALL_ROOT_1}/workspaces"
echo "fixture" > "${INSTALL_ROOT_1}/host/dispatcher.env"

if run_uninstall "${HOME_DIR_1}" --yes --force --purge >"${TMP_BASE}/uninstall2.out" 2>&1; then
  ok "uninstall --purge exit 0"
else
  bad "uninstall --purge failed"
  cat "${TMP_BASE}/uninstall2.out" >&2
fi
assert_path_absent "${INSTALL_ROOT_1}/spring.env" "spring.env removed under --purge"
assert_path_absent "${INSTALL_ROOT_1}/host" "host/ removed under --purge"
assert_path_absent "${INSTALL_ROOT_1}/workspaces" "workspaces/ removed under --purge"

# ===========================================================================
# Case 7: uninstall idempotent
# ===========================================================================
hdr "Case 7 — uninstall is idempotent"
if run_uninstall "${HOME_DIR_1}" --yes --force >"${TMP_BASE}/uninstall3.out" 2>&1; then
  ok "second uninstall exit 0 (idempotent)"
else
  bad "second uninstall failed"
  cat "${TMP_BASE}/uninstall3.out" >&2
fi

# ===========================================================================
# Case 8: stale dispatcher PID (alive) fails install
# ===========================================================================
hdr "Case 8 — stale dispatcher (alive PID) fails install"
HOME_DIR_8="${TMP_BASE}/home-8"
STUB_DIR_8="${TMP_BASE}/stub-8"
mkdir -p "${HOME_DIR_8}/.spring-voyage/host"
make_stub_path "${STUB_DIR_8}" "Linux" "x86_64"
# Use the test runner's own PID — guaranteed alive for the duration of the run.
echo "$$" > "${HOME_DIR_8}/.spring-voyage/host/spring-dispatcher.pid"
if run_install "${HOME_DIR_8}" "${STUB_DIR_8}" --no-start >"${TMP_BASE}/run8.out" 2>&1; then
  bad "install.sh succeeded with a live stale dispatcher PID; expected failure"
else
  ok "install.sh refused install with a live stale dispatcher PID"
fi
if grep -q "Stale dispatcher process detected" "${TMP_BASE}/run8.out"; then
  ok "output mentions 'Stale dispatcher process detected'"
else
  bad "output missing 'Stale dispatcher process detected'"
  cat "${TMP_BASE}/run8.out" >&2
fi
# PID file must NOT be silently removed when the process is alive — operator
# expects to see it after the message so they can act on the PID.
assert_path_exists "${HOME_DIR_8}/.spring-voyage/host/spring-dispatcher.pid" "live stale PID file preserved"

# ===========================================================================
# Case 9: dead PID file is cleaned up silently and install proceeds
# ===========================================================================
hdr "Case 9 — dead dispatcher PID file is cleaned up silently"
HOME_DIR_9="${TMP_BASE}/home-9"
STUB_DIR_9="${TMP_BASE}/stub-9"
mkdir -p "${HOME_DIR_9}/.spring-voyage/host"
make_stub_path "${STUB_DIR_9}" "Linux" "x86_64"
# Spawn and immediately reap a subshell to get a recently-dead PID. The
# kernel will not have re-used it within the test's lifetime on any
# reasonable system.
( exec sleep 0 ) &
dead_pid="$!"
wait "$dead_pid" 2>/dev/null || true
# Belt and braces: also try a clearly-out-of-range PID if 'kill -0' would
# still say the recycled PID is alive. Pick a high PID; kill -0 will report
# ESRCH on any sane host because no process with PID 999999 exists.
if kill -0 "$dead_pid" 2>/dev/null; then
  dead_pid="999999"
fi
echo "$dead_pid" > "${HOME_DIR_9}/.spring-voyage/host/spring-dispatcher.pid"
if run_install "${HOME_DIR_9}" "${STUB_DIR_9}" --no-start >"${TMP_BASE}/run9.out" 2>&1; then
  ok "install.sh succeeded past dead PID file"
else
  bad "install.sh failed despite PID file pointing at a dead process"
  cat "${TMP_BASE}/run9.out" >&2
fi
assert_path_absent "${HOME_DIR_9}/.spring-voyage/host/spring-dispatcher.pid" "dead PID file silently removed"

# ===========================================================================
# Case 10: --force bypasses stale-dispatcher check
# ===========================================================================
hdr "Case 10 — --force bypasses stale-dispatcher check"
HOME_DIR_10="${TMP_BASE}/home-10"
STUB_DIR_10="${TMP_BASE}/stub-10"
mkdir -p "${HOME_DIR_10}/.spring-voyage/host"
make_stub_path "${STUB_DIR_10}" "Linux" "x86_64"
echo "$$" > "${HOME_DIR_10}/.spring-voyage/host/spring-dispatcher.pid"
if run_install "${HOME_DIR_10}" "${STUB_DIR_10}" --no-start --force >"${TMP_BASE}/run10.out" 2>&1; then
  ok "install.sh --force proceeded past live stale dispatcher"
else
  bad "install.sh --force still failed on stale dispatcher"
  cat "${TMP_BASE}/run10.out" >&2
fi
if grep -q "Stale dispatcher PID.*alive" "${TMP_BASE}/run10.out"; then
  ok "--force emitted stale-dispatcher warning"
else
  bad "--force did not emit stale-dispatcher warning"
  cat "${TMP_BASE}/run10.out" >&2
fi

# ===========================================================================
# Deterministic host-port availability stub (used by Cases 11 and 13-15).
# ===========================================================================
# install.sh's port_in_use prefers lsof; this stub shadows it so a port reads
# as "in use" iff it appears in the space-separated busy list (free otherwise),
# making the port cases independent of the test host's real 80/443/5050 state
# (and of whether python3 is installed).
stub_ports() {
  local dir="$1"; shift
  local busy="$*"
  cat > "${dir}/lsof" <<LSOF
#!/usr/bin/env bash
# install.sh calls: lsof -nP -iTCP:<port> -sTCP:LISTEN
p=""
for a in "\$@"; do case "\$a" in -iTCP:*) p="\${a#-iTCP:}";; esac; done
for b in ${busy}; do [[ "\${p}" == "\${b}" ]] && exit 0; done
exit 1
LSOF
  chmod +x "${dir}/lsof"
}

# ===========================================================================
# Case 11: dispatcher port conflict fails install
# ===========================================================================
# A controllable lsof stub marks the dispatcher port in use, so detection is
# deterministic and independent of the host (and of python3 being installed).
hdr "Case 11 — dispatcher port conflict fails install"
HOME_DIR_11="${TMP_BASE}/home-11"
STUB_DIR_11="${TMP_BASE}/stub-11"
mkdir -p "${HOME_DIR_11}"
make_stub_path "${STUB_DIR_11}" "Linux" "x86_64"
TEST_DISPATCHER_PORT="19790"
stub_ports "${STUB_DIR_11}" "${TEST_DISPATCHER_PORT}"
if SPRING_DISPATCHER_PORT="${TEST_DISPATCHER_PORT}" \
   run_install_with_port_check "${HOME_DIR_11}" "${STUB_DIR_11}" --no-start \
   >"${TMP_BASE}/run11.out" 2>&1; then
  bad "install.sh succeeded with dispatcher port ${TEST_DISPATCHER_PORT} in use; expected failure"
else
  ok "install.sh refused install with dispatcher port ${TEST_DISPATCHER_PORT} in use"
fi
if grep -q "already in use" "${TMP_BASE}/run11.out" && grep -q "${TEST_DISPATCHER_PORT}" "${TMP_BASE}/run11.out"; then
  ok "output mentions the conflicting dispatcher port ${TEST_DISPATCHER_PORT}"
else
  bad "output missing 'already in use' or the port number"
  cat "${TMP_BASE}/run11.out" >&2
fi

# ===========================================================================
# Wrapper subcommands (#2173) — these run against a fresh install fixture so
# we don't depend on Cases 5–10 having mutated HOME_DIR_1's filesystem.
# ===========================================================================
hdr "Case 12 — wrapper: version + status + logs + restart + help"
HOME_DIR_W="${TMP_BASE}/home-wrapper"
STUB_DIR_W="${TMP_BASE}/stub-wrapper"
mkdir -p "${HOME_DIR_W}"
make_stub_path "${STUB_DIR_W}" "Linux" "x86_64"
if run_install "${HOME_DIR_W}" "${STUB_DIR_W}" --no-start >"${TMP_BASE}/run-wrapper-install.out" 2>&1; then
  ok "install for wrapper tests exit 0"
else
  bad "install for wrapper tests failed"
  cat "${TMP_BASE}/run-wrapper-install.out" >&2
fi

WRAPPER_W="${HOME_DIR_W}/.local/bin/voyage"
assert_path_exists "${WRAPPER_W}" "wrapper installed at ~/.local/bin/voyage"

# version: prints the fixture version + platform image. Run with
# SPRING_VOYAGE_HOME pointing at the test install so the wrapper finds
# the right manifest.json without depending on the real $HOME.
if SPRING_VOYAGE_HOME="${HOME_DIR_W}/.spring-voyage" \
   bash "${WRAPPER_W}" version >"${TMP_BASE}/wrapper-version.out" 2>&1; then
  ok "voyage version exit 0"
else
  bad "voyage version exited non-zero"
  cat "${TMP_BASE}/wrapper-version.out" >&2
fi
if grep -q "voyage version ${FIXTURE_VERSION}" "${TMP_BASE}/wrapper-version.out"; then
  ok "voyage version prints fixture version ${FIXTURE_VERSION}"
else
  bad "voyage version missing fixture version"
  cat "${TMP_BASE}/wrapper-version.out" >&2
fi
if grep -q "platform image" "${TMP_BASE}/wrapper-version.out"; then
  ok "voyage version prints platform image tag"
else
  bad "voyage version missing platform image line"
fi

# The fixture deploy.sh records invocations to deploy.log; we use that to
# assert that status / restart / logs reach it.
DEPLOY_LOG_W="${HOME_DIR_W}/.spring-voyage/deploy.log"
: > "${DEPLOY_LOG_W}"

# status with no dispatcher PID file → reports "not running".
rm -f "${HOME_DIR_W}/.spring-voyage/host/spring-dispatcher.pid"
if SPRING_VOYAGE_HOME="${HOME_DIR_W}/.spring-voyage" \
   bash "${WRAPPER_W}" status >"${TMP_BASE}/wrapper-status-down.out" 2>&1; then
  ok "voyage status (no dispatcher) exit 0"
else
  bad "voyage status (no dispatcher) exited non-zero"
  cat "${TMP_BASE}/wrapper-status-down.out" >&2
fi
if grep -q "not running" "${TMP_BASE}/wrapper-status-down.out"; then
  ok "status reports dispatcher 'not running' when no PID file"
else
  bad "status missing 'not running' dispatcher line"
  cat "${TMP_BASE}/wrapper-status-down.out" >&2
fi
if grep -q "version ${FIXTURE_VERSION}" "${TMP_BASE}/wrapper-status-down.out"; then
  ok "status includes manifest version"
else
  bad "status missing manifest version line"
fi
if grep -q "deploy.sh status" "${DEPLOY_LOG_W}"; then
  ok "status delegated container check to deploy.sh"
else
  bad "deploy.log missing 'deploy.sh status' entry"
fi

# status with a live PID (use our own — guaranteed alive) → reports "running (PID N)".
mkdir -p "${HOME_DIR_W}/.spring-voyage/host"
echo "$$" > "${HOME_DIR_W}/.spring-voyage/host/spring-dispatcher.pid"
if SPRING_VOYAGE_HOME="${HOME_DIR_W}/.spring-voyage" \
   bash "${WRAPPER_W}" status >"${TMP_BASE}/wrapper-status-up.out" 2>&1; then
  ok "voyage status (live dispatcher) exit 0"
else
  bad "voyage status (live dispatcher) exited non-zero"
  cat "${TMP_BASE}/wrapper-status-up.out" >&2
fi
if grep -qE "running \(PID $$\)" "${TMP_BASE}/wrapper-status-up.out"; then
  ok "status reports 'running (PID N)' for live dispatcher"
else
  bad "status missing 'running (PID $$)' line"
  cat "${TMP_BASE}/wrapper-status-up.out" >&2
fi
rm -f "${HOME_DIR_W}/.spring-voyage/host/spring-dispatcher.pid"

# logs dispatcher → tails the dispatcher log file. Stub `tail` via a
# PATH-injected dir that records args; the wrapper uses `exec tail -F` so
# the stub just needs to record args and exit 0.
LOGS_STUB_DIR="${TMP_BASE}/stub-logs"
mkdir -p "${LOGS_STUB_DIR}"
cat > "${LOGS_STUB_DIR}/tail" <<TAIL
#!/usr/bin/env bash
printf '%s\n' "tail \$*" > "${TMP_BASE}/tail-args.out"
exit 0
TAIL
chmod +x "${LOGS_STUB_DIR}/tail"

# Ensure the dispatcher log file exists so the wrapper doesn't bail early.
mkdir -p "${HOME_DIR_W}/.spring-voyage/host"
touch "${HOME_DIR_W}/.spring-voyage/host/spring-dispatcher.log"

if PATH="${LOGS_STUB_DIR}:${PATH}" \
   SPRING_VOYAGE_HOME="${HOME_DIR_W}/.spring-voyage" \
   bash "${WRAPPER_W}" logs dispatcher >"${TMP_BASE}/wrapper-logs-dispatcher.out" 2>&1; then
  ok "voyage logs dispatcher exit 0"
else
  bad "voyage logs dispatcher exited non-zero"
  cat "${TMP_BASE}/wrapper-logs-dispatcher.out" >&2
fi
if [[ -f "${TMP_BASE}/tail-args.out" ]] && \
   grep -q "tail -F .*spring-dispatcher.log" "${TMP_BASE}/tail-args.out"; then
  ok "logs dispatcher invokes 'tail -F <dispatcher.log>'"
else
  bad "logs dispatcher did not invoke 'tail -F <dispatcher.log>'"
  [[ -f "${TMP_BASE}/tail-args.out" ]] && cat "${TMP_BASE}/tail-args.out" >&2
fi

# logs <service> → delegates to deploy.sh logs <service>.
: > "${DEPLOY_LOG_W}"
if SPRING_VOYAGE_HOME="${HOME_DIR_W}/.spring-voyage" \
   bash "${WRAPPER_W}" logs spring-api >"${TMP_BASE}/wrapper-logs-svc.out" 2>&1; then
  ok "voyage logs <service> exit 0"
else
  bad "voyage logs <service> exited non-zero"
  cat "${TMP_BASE}/wrapper-logs-svc.out" >&2
fi
if grep -q "deploy.sh logs spring-api" "${DEPLOY_LOG_W}"; then
  ok "logs <service> delegated to deploy.sh logs spring-api"
else
  bad "deploy.log missing 'deploy.sh logs spring-api' entry"
  cat "${DEPLOY_LOG_W}" >&2
fi

# restart → delegates to deploy.sh restart.
: > "${DEPLOY_LOG_W}"
if SPRING_VOYAGE_HOME="${HOME_DIR_W}/.spring-voyage" \
   bash "${WRAPPER_W}" restart >"${TMP_BASE}/wrapper-restart.out" 2>&1; then
  ok "voyage restart exit 0"
else
  bad "voyage restart exited non-zero"
  cat "${TMP_BASE}/wrapper-restart.out" >&2
fi
if grep -q "deploy.sh restart" "${DEPLOY_LOG_W}"; then
  ok "restart delegated to deploy.sh restart"
else
  bad "deploy.log missing 'deploy.sh restart' entry"
  cat "${DEPLOY_LOG_W}" >&2
fi

# --help, help, and bare invocation all exit 0 with usage that lists the
# new subcommands.
for help_args in "--help" "-h" "help" ""; do
  label="${help_args:-<bare>}"
  if SPRING_VOYAGE_HOME="${HOME_DIR_W}/.spring-voyage" \
     bash "${WRAPPER_W}" ${help_args} >"${TMP_BASE}/wrapper-help-${label}.out" 2>&1; then
    ok "voyage ${label} exit 0"
  else
    bad "voyage ${label} exited non-zero"
    cat "${TMP_BASE}/wrapper-help-${label}.out" >&2
  fi
  if grep -q "^  status " "${TMP_BASE}/wrapper-help-${label}.out" \
     && grep -q "^  logs " "${TMP_BASE}/wrapper-help-${label}.out" \
     && grep -q "^  restart " "${TMP_BASE}/wrapper-help-${label}.out" \
     && grep -q "^  version " "${TMP_BASE}/wrapper-help-${label}.out"; then
    ok "voyage ${label} usage lists status / logs / restart / version"
  else
    bad "voyage ${label} usage missing one of status / logs / restart / version"
    cat "${TMP_BASE}/wrapper-help-${label}.out" >&2
  fi
done

# ===========================================================================
# Case 13: rootless privileged port — --yes fails fast with both remedies
# ===========================================================================
# Floor pinned to 1024 via SPRING_INSTALL_UNPRIV_PORT_START; Caddy ports 81/444
# are below it (and free on any host), so a rootless bind would fail. The
# dispatcher/MCP checks are pushed to high free ports so only the privileged-
# port path is under test. The uname stub reports Linux so the (Linux-only)
# check engages.
hdr "Case 13 — rootless privileged port fails fast under --yes"
HOME_DIR_13="${TMP_BASE}/home-13"
STUB_DIR_13="${TMP_BASE}/stub-13"
mkdir -p "${HOME_DIR_13}"
make_stub_path "${STUB_DIR_13}" "Linux" "x86_64"
stub_ports "${STUB_DIR_13}"
if SPRING_INSTALL_UNPRIV_PORT_START=1024 \
   CADDY_HTTP_PORT=81 CADDY_HTTPS_PORT=444 \
   SPRING_DISPATCHER_PORT=18190 Mcp__Port=18191 \
   run_install_with_port_check "${HOME_DIR_13}" "${STUB_DIR_13}" --no-start \
   >"${TMP_BASE}/run13.out" 2>&1; then
  bad "install.sh succeeded with sub-floor Caddy ports; expected rootless fail-fast"
else
  ok "install.sh failed fast on the rootless privileged-port condition"
fi
if grep -q "ip_unprivileged_port_start" "${TMP_BASE}/run13.out" \
   && grep -q "CADDY_HTTP_PORT=8080" "${TMP_BASE}/run13.out" \
   && grep -q "sudo sysctl" "${TMP_BASE}/run13.out"; then
  ok "fail-fast output offers both remedies (lower threshold + high ports)"
else
  bad "fail-fast output missing the privileged-port remedies"
  cat "${TMP_BASE}/run13.out" >&2
fi
# The check is in pre-flight, before any download — nothing should be written.
assert_path_absent "${HOME_DIR_13}/.spring-voyage/current"    "no current symlink after fail-fast"
assert_path_absent "${HOME_DIR_13}/.spring-voyage/spring.env" "no spring.env written after fail-fast"

# ===========================================================================
# Case 14: operator-preset high ports bypass the privileged-port check
# ===========================================================================
hdr "Case 14 — preset high ports bypass the privileged-port check"
HOME_DIR_14="${TMP_BASE}/home-14"
STUB_DIR_14="${TMP_BASE}/stub-14"
mkdir -p "${HOME_DIR_14}"
make_stub_path "${STUB_DIR_14}" "Linux" "x86_64"
stub_ports "${STUB_DIR_14}"
if SPRING_INSTALL_UNPRIV_PORT_START=1024 \
   CADDY_HTTP_PORT=18080 CADDY_HTTPS_PORT=18443 \
   SPRING_DISPATCHER_PORT=18190 Mcp__Port=18191 \
   run_install_with_port_check "${HOME_DIR_14}" "${STUB_DIR_14}" --no-start \
   >"${TMP_BASE}/run14.out" 2>&1; then
  ok "install.sh proceeds with preset high ports under a 1024 floor"
else
  bad "install.sh failed despite preset high Caddy ports"
  cat "${TMP_BASE}/run14.out" >&2
fi
ENV_FILE_14="${HOME_DIR_14}/.spring-voyage/spring.env"
assert_contains "${ENV_FILE_14}" "^CADDY_HTTP_PORT=18080$"  "spring.env pins preset CADDY_HTTP_PORT"
assert_contains "${ENV_FILE_14}" "^CADDY_HTTPS_PORT=18443$" "spring.env pins preset CADDY_HTTPS_PORT"

# ===========================================================================
# Case 15: already-lowered floor leaves default 80/443 untouched
# ===========================================================================
hdr "Case 15 — already-lowered floor leaves default 80/443 untouched"
HOME_DIR_15="${TMP_BASE}/home-15"
STUB_DIR_15="${TMP_BASE}/stub-15"
mkdir -p "${HOME_DIR_15}"
make_stub_path "${STUB_DIR_15}" "Linux" "x86_64"
stub_ports "${STUB_DIR_15}"
# Floor already at 80 → default 80/443 are bindable; no remap, no prompt, and
# no CADDY_*_PORT override should be written to spring.env.
if SPRING_INSTALL_UNPRIV_PORT_START=80 \
   CADDY_HTTP_PORT='' CADDY_HTTPS_PORT='' \
   SPRING_DISPATCHER_PORT=18190 Mcp__Port=18191 \
   run_install_with_port_check "${HOME_DIR_15}" "${STUB_DIR_15}" --no-start \
   >"${TMP_BASE}/run15.out" 2>&1; then
  ok "install.sh proceeds with default 80/443 when the floor is already low"
else
  bad "install.sh failed with default ports despite a low unprivileged floor"
  cat "${TMP_BASE}/run15.out" >&2
fi
ENV_FILE_15="${HOME_DIR_15}/.spring-voyage/spring.env"
if grep -qE "^CADDY_HTTP_PORT=" "${ENV_FILE_15}" 2>/dev/null; then
  bad "spring.env should not pin CADDY_HTTP_PORT when default 80 is bindable"
  cat "${ENV_FILE_15}" >&2
else
  ok "spring.env leaves CADDY_HTTP_PORT at its default (no override written)"
fi
if grep -q "bindable under rootless Podman" "${TMP_BASE}/run15.out"; then
  ok "install confirms Caddy ports are bindable (floor satisfied)"
else
  bad "install did not confirm rootless bindability"
  cat "${TMP_BASE}/run15.out" >&2
fi

# ===========================================================================
# Summary
# ===========================================================================
hdr "Summary"
printf "  passed: %d\n  failed: %d\n" "$PASS" "$FAIL"
[[ "$FAIL" -eq 0 ]]
