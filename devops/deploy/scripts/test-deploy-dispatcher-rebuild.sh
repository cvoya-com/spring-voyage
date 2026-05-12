#!/usr/bin/env bash
# Regression guard for #2220: deploy.sh must bounce the host dispatcher with
# --rebuild so `deploy.sh up` republishes the dispatcher binary before restart.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOYMENT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

TMP_DIR="$(mktemp -d -t spring-deploy-dispatcher-rebuild.XXXXXX)"
trap 'rm -rf "${TMP_DIR}"' EXIT

HOST_STUB="${TMP_DIR}/spring-voyage-host.sh"
ARGS_LOG="${TMP_DIR}/host-args.log"
DEPLOY_LIB="${TMP_DIR}/deploy-lib.sh"

cat >"${HOST_STUB}" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "${SPRING_HOST_STUB_ARGS_LOG:?}"
STUB
chmod +x "${HOST_STUB}"

# Load deploy.sh's function definitions without running its main dispatch.
sed '/^main "\$@"/d' "${DEPLOYMENT_DIR}/deploy.sh" >"${DEPLOY_LIB}"
# shellcheck disable=SC1090
source "${DEPLOY_LIB}"

export SPRING_HOST_STUB_ARGS_LOG="${ARGS_LOG}"
REPO_ROOT="${TMP_DIR}"
HOST_SCRIPT="${HOST_STUB}"

start_dispatcher >/dev/null 2>&1

ACTUAL="$(cat "${ARGS_LOG}")"
EXPECTED="restart --rebuild"
if [[ "${ACTUAL}" != "${EXPECTED}" ]]; then
    printf '[FAIL] start_dispatcher invoked host script with %q; expected %q\n' "${ACTUAL}" "${EXPECTED}" >&2
    exit 1
fi

printf '[ ok ] deploy.sh start_dispatcher invokes spring-voyage-host.sh restart --rebuild\n'
