#!/usr/bin/env bash
# Dry-run / fixture-driven unit tests for devops/install/install.sh.
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
WRAPPER_SH="${INSTALL_DIR}/spring-voyage"

[[ -x "${INSTALL_SH}" ]] || { echo "install.sh not executable: ${INSTALL_SH}" >&2; exit 1; }
[[ -x "${UNINSTALL_SH}" ]] || { echo "uninstall.sh not executable: ${UNINSTALL_SH}" >&2; exit 1; }
[[ -x "${WRAPPER_SH}" ]] || { echo "spring-voyage wrapper not executable: ${WRAPPER_SH}" >&2; exit 1; }

FIXTURE_VERSION="0.0.0-test"
FIXTURE_TAG="v${FIXTURE_VERSION}"
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
  # release pipelines copy uninstall.sh into the bundle from devops/install/.
  cp "${UNINSTALL_SH}" "${stage}/uninstall.sh"
  chmod +x "${stage}/uninstall.sh"

  # Fixture spring-voyage wrapper — same rationale as uninstall.sh above.
  # The real release-pipeline copies devops/install/spring-voyage into the
  # bundle so install.sh can `cp` it to ~/.local/bin/.
  cp "${WRAPPER_SH}" "${stage}/spring-voyage"
  chmod +x "${stage}/spring-voyage"

  # Dapr components placeholder.
  echo '# fixture' > "${stage}/dapr/components/delegated-spring-voyage-agent/README.md"

  # manifest.json.
  cat > "${stage}/manifest.json" <<EOFMANIFEST
{
  "bundle_schema_version": 1,
  "version": "${FIXTURE_VERSION}",
  "platform_image": "${PLATFORM_IMAGE}",
  "dispatcher_version": "${FIXTURE_VERSION}",
  "cli_version": "${FIXTURE_VERSION}"
}
EOFMANIFEST

  # Build bundle.tar.gz with the leading `bundle/` directory (matching
  # the real release-pipeline shape).
  ( cd "${TMP_BASE}/stage" && tar -czf "${rel}/spring-voyage-${FIXTURE_VERSION}-bundle.tar.gz" bundle )

  # Dispatcher stage.
  local disp_stage="${TMP_BASE}/stage-dispatcher"
  rm -rf "${disp_stage}"; mkdir -p "${disp_stage}"
  cat > "${disp_stage}/Cvoya.Spring.Dispatcher" <<'DISPATCHER'
#!/usr/bin/env bash
echo "fixture dispatcher: $*"
DISPATCHER
  chmod +x "${disp_stage}/Cvoya.Spring.Dispatcher"

  # CLI stage.
  local cli_stage="${TMP_BASE}/stage-cli"
  rm -rf "${cli_stage}"; mkdir -p "${cli_stage}"
  cat > "${cli_stage}/spring" <<'CLI'
#!/usr/bin/env bash
# Fixture spring CLI — supports `spring github-app register --name ... --env-path ... --write-env`
# Records the invocation and exits 0 unless ${SPRING_FIXTURE_GH_REGISTER_FAIL} is set.
printf '%s\n' "spring $*" >> "${SPRING_FIXTURE_CLI_LOG:-/dev/null}"
if [[ -n "${SPRING_FIXTURE_GH_REGISTER_FAIL:-}" ]]; then exit 1; fi
exit 0
CLI
  chmod +x "${cli_stage}/spring"

  # Per-RID archives. The same fixture binary is reused; the installer
  # never executes them in dry-run, it only checks they exist after
  # extraction.
  local rids=(linux-x64 linux-arm64 osx-x64 osx-arm64)
  for rid in "${rids[@]}"; do
    tar -C "${disp_stage}" -czf "${rel}/spring-dispatcher-${FIXTURE_VERSION}-${rid}.tar.gz" .
    tar -C "${cli_stage}"  -czf "${rel}/spring-${FIXTURE_VERSION}-${rid}.tar.gz" .
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
# test fixture controls.
run_install_with_port_check() {
  local home_dir="$1"; shift
  local stub_dir="$1"; shift
  HOME="${home_dir}" \
    SPRING_VOYAGE_VERSION="${FIXTURE_VERSION}" \
    FIXTURE_RELEASE_DIR="${TMP_BASE}/release" \
    SPRING_FIXTURE_PODMAN_LOG="${home_dir}/.spring-voyage/podman.log" \
    SPRING_FIXTURE_CLI_LOG="${home_dir}/.spring-voyage/cli.log" \
    SPRING_DISPATCHER_PORT="${SPRING_DISPATCHER_PORT:-}" \
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
assert_path_exists "${HOME_DIR_1}/.local/bin/spring-voyage" "spring-voyage wrapper exists"
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
assert_path_absent "${HOME_DIR_1}/.local/bin/spring-voyage" "spring-voyage wrapper removed"
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
# Case 11: dispatcher port conflict fails install
# ===========================================================================
# We bind a TCP listener on a high free port and tell the installer to treat
# that port as the dispatcher port (via SPRING_DISPATCHER_PORT). This avoids
# colliding with 8090 if the developer machine has a real dispatcher running.
hdr "Case 11 — dispatcher port conflict fails install"
HOME_DIR_11="${TMP_BASE}/home-11"
STUB_DIR_11="${TMP_BASE}/stub-11"
mkdir -p "${HOME_DIR_11}"
make_stub_path "${STUB_DIR_11}" "Linux" "x86_64"

# Pick a high port and bind it with python3. We require python3 here because
# `nc -l` semantics differ wildly between BSD/macOS netcat and GNU netcat.
if ! command -v python3 >/dev/null 2>&1; then
  note "skipping Case 11: python3 not available to bind the test port"
else
  TEST_DISPATCHER_PORT="19790"
  python3 -c "
import socket, sys, time
s = socket.socket()
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(('127.0.0.1', ${TEST_DISPATCHER_PORT}))
s.listen()
sys.stdout.write('ready\n'); sys.stdout.flush()
time.sleep(30)
" > "${TMP_BASE}/listener.out" &
  LISTENER_PID="$!"
  # Wait for 'ready' or up to ~3s.
  for _ in 1 2 3 4 5 6; do
    if grep -q ready "${TMP_BASE}/listener.out" 2>/dev/null; then break; fi
    sleep 0.5
  done
  if ! kill -0 "$LISTENER_PID" 2>/dev/null; then
    bad "test listener failed to start on port ${TEST_DISPATCHER_PORT}"
  else
    if SPRING_DISPATCHER_PORT="${TEST_DISPATCHER_PORT}" \
       run_install_with_port_check "${HOME_DIR_11}" "${STUB_DIR_11}" --no-start \
       >"${TMP_BASE}/run11.out" 2>&1; then
      bad "install.sh succeeded with dispatcher port ${TEST_DISPATCHER_PORT} bound; expected failure"
    else
      ok "install.sh refused install with dispatcher port ${TEST_DISPATCHER_PORT} bound"
    fi
    if grep -q "already bound" "${TMP_BASE}/run11.out" && grep -q "${TEST_DISPATCHER_PORT}" "${TMP_BASE}/run11.out"; then
      ok "output mentions the conflicting dispatcher port ${TEST_DISPATCHER_PORT}"
    else
      bad "output missing 'already bound' or the port number"
      cat "${TMP_BASE}/run11.out" >&2
    fi
    kill "$LISTENER_PID" 2>/dev/null || true
    wait "$LISTENER_PID" 2>/dev/null || true
  fi
fi

# ===========================================================================
# Summary
# ===========================================================================
hdr "Summary"
printf "  passed: %d\n  failed: %d\n" "$PASS" "$FAIL"
[[ "$FAIL" -eq 0 ]]
