#!/usr/bin/env bash
# Tier-1 dispatcher smoke test.
#
# Drives a single round-trip through the host-process dispatcher:
#   1. Start the dispatcher via deployment/spring-voyage-host.sh.
#   2. Pull the bearer token from ${SPRING_HOST_STATE_DIR}/dispatcher.env.
#   3. POST /v1/containers with a tiny docker.io/library/alpine:latest
#      image running `echo ok`, timeout 120s.
#   4. Assert the response is HTTP 200, exitCode == 0, stdout contains
#      "ok", and the dispatcher log is free of "exit-125" markers
#      (regression sentinel for #1063).
#   5. Stop the dispatcher.
#
# This script is what the .github/workflows/ci.yml `dispatcher-smoke`
# job runs on every PR. It is also the script operators run locally
# to verify the host-process pivot still works on macOS arm64 — see
# deployment/README.md for the manual procedure.
#
# Requires podman or docker on PATH (the runner image must provide
# one). The dispatcher process resolves the binary via
# ContainerRuntime__RuntimeType (defaults to "podman").

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOYMENT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

HOST_SCRIPT="${DEPLOYMENT_DIR}/spring-voyage-host.sh"
[[ -x "${HOST_SCRIPT}" ]] || { echo "host script not executable: ${HOST_SCRIPT}" >&2; exit 1; }

# Honour caller-supplied state/publish dirs so this script can be
# reused inside larger drivers (the Tier-2 deploy.sh smoke also wraps
# this); fall back to isolated temp dirs otherwise.
STATE_DIR="${SPRING_HOST_STATE_DIR:-$(mktemp -d -t dispatcher-smoke-state.XXXXXX)}"
PUBLISH_DIR="${SPRING_DISPATCHER_PUBLISH_DIR:-$(mktemp -d -t dispatcher-smoke-publish.XXXXXX)}"
WORKSPACE_ROOT="${SPRING_DISPATCHER_WORKSPACE_ROOT:-$(mktemp -d -t dispatcher-smoke-workspaces.XXXXXX)}"
TEST_HOST="${SPRING_DISPATCHER_HOST:-127.0.0.1}"
TEST_PORT="${SPRING_DISPATCHER_PORT:-18091}"

export SPRING_HOST_STATE_DIR="${STATE_DIR}"
export SPRING_DISPATCHER_PUBLISH_DIR="${PUBLISH_DIR}"
export SPRING_DISPATCHER_WORKSPACE_ROOT="${WORKSPACE_ROOT}"
export SPRING_DISPATCHER_HOST="${TEST_HOST}"
export SPRING_DISPATCHER_PORT="${TEST_PORT}"
unset SPRING_DISPATCHER_WORKER_TOKEN
export SPRING_ENV_FILE="${STATE_DIR}/empty.env"
: >"${SPRING_ENV_FILE}"

# Pick the runtime that's actually available. Most GitHub-hosted
# Ubuntu runners ship podman; macOS dev hosts ship both. Either
# satisfies the dispatcher's startup probe; the dispatcher's
# PodmanRuntime always invokes the literal `podman` binary, so on a
# docker-only host this script would fail at the dispatch step (not
# at boot). Surface that early.
if command -v podman >/dev/null 2>&1; then
    RUNTIME=podman
elif command -v docker >/dev/null 2>&1; then
    RUNTIME=docker
    echo "[smoke][warn] only docker is on PATH; the dispatcher's PodmanRuntime always shells out to 'podman' so the dispatch step will fail. Install podman to run the smoke test." >&2
else
    echo "[smoke] neither podman nor docker is on PATH" >&2
    exit 2
fi
export ContainerRuntime__RuntimeType="${RUNTIME}"
echo "[smoke] using container runtime: ${RUNTIME}"

LOG_FILE="${STATE_DIR}/spring-dispatcher.log"

cleanup() {
    local code=$?
    set +e
    "${HOST_SCRIPT}" stop >/dev/null 2>&1 || true
    if [[ ${code} -ne 0 && -f "${LOG_FILE}" ]]; then
        echo "----- dispatcher log tail -----" >&2
        tail -n 80 "${LOG_FILE}" >&2 || true
        echo "-------------------------------" >&2
    fi
    if [[ "${KEEP_SMOKE_DIRS:-0}" -eq 0 && -z "${SPRING_HOST_STATE_DIR_PRESET:-}" ]]; then
        rm -rf "${STATE_DIR}" "${PUBLISH_DIR}" "${WORKSPACE_ROOT}"
    fi
    exit "${code}"
}
trap cleanup EXIT INT TERM

echo "[smoke] starting dispatcher"
"${HOST_SCRIPT}" start >/dev/null

ENV_FILE="${STATE_DIR}/dispatcher.env"
[[ -f "${ENV_FILE}" ]] || { echo "[smoke] dispatcher.env was not written"; exit 1; }
TOKEN="$(grep -E '^SPRING_DISPATCHER_WORKER_TOKEN=' "${ENV_FILE}" | tail -n1)"
TOKEN="${TOKEN#SPRING_DISPATCHER_WORKER_TOKEN=}"
[[ -n "${TOKEN}" ]] || { echo "[smoke] no token in dispatcher.env"; exit 1; }

URL="http://${TEST_HOST}:${TEST_PORT}/v1/containers"
echo "[smoke] POST ${URL} (alpine:latest, echo ok)"

REQUEST_BODY='{"image":"docker.io/library/alpine:latest","command":"echo ok","timeoutSeconds":120}'

RESPONSE_BODY="$(mktemp)"
HTTP_CODE="$(curl -sS -o "${RESPONSE_BODY}" -w '%{http_code}' \
    --max-time 180 \
    -H "Authorization: Bearer ${TOKEN}" \
    -H 'Content-Type: application/json' \
    -X POST \
    -d "${REQUEST_BODY}" \
    "${URL}" || echo 000)"

if [[ "${HTTP_CODE}" != "200" ]]; then
    echo "[smoke] expected HTTP 200, got ${HTTP_CODE}"
    echo "----- response body -----"
    cat "${RESPONSE_BODY}" || true
    echo
    echo "-------------------------"
    rm -f "${RESPONSE_BODY}"
    exit 1
fi

# Use python because jq isn't guaranteed on every runner image.
EXIT_CODE="$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d.get("exitCode"))' "${RESPONSE_BODY}")"
STDOUT_VALUE="$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d.get("stdout") or "")' "${RESPONSE_BODY}")"
rm -f "${RESPONSE_BODY}"

if [[ "${EXIT_CODE}" != "0" ]]; then
    echo "[smoke] container exitCode=${EXIT_CODE} (expected 0)"
    exit 1
fi
if ! grep -q '^ok$' <<<"${STDOUT_VALUE}"; then
    echo "[smoke] stdout did not contain 'ok' on its own line: ${STDOUT_VALUE}"
    exit 1
fi

# Regression sentinel for #1063: the libkrun socket-passthrough bug
# manifested as `podman ... ; exit code: 125` in the dispatcher log
# on every dispatch. If we ever see exit code 125 from podman in the
# log, the host-process pivot is broken on this runner.
if grep -E 'Exit code: 125|exit-125|exit code 125' "${LOG_FILE}" >/dev/null 2>&1; then
    echo "[smoke] dispatcher log shows exit-125 — #1063 regression"
    grep -E 'Exit code: 125|exit-125|exit code 125' "${LOG_FILE}" | head -5 >&2
    exit 1
fi

echo "[smoke] dispatcher round-trip succeeded (exitCode=0, stdout='ok', no exit-125 in log)"
