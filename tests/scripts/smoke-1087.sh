#!/usr/bin/env bash
# tests/scripts/smoke-1087.sh — end-to-end conformance smoke for the unified
# agent dispatch path (PR 6 of #1087, docs / acceptance #1099).
#
# This smoke exercises the BYOI conformance contract from ADR
# `docs/decisions/0027-agent-image-conformance-contract.md` end-to-end at
# the wire level: it boots an agent image, polls /.well-known/agent.json
# until ready, fires an A2A `message/send`, and asserts a real response
# came back (status.state == "TASK_STATE_COMPLETED", artifact text
# matches). Enum values follow the proto-style names that the .NET A2A
# SDK pins via [JsonStringEnumMemberName]; see issue #1115 for the
# rationale.
#
# What's exercised today:
#   - Path 1 (FROM ghcr.io/cvoya-com/agent-base): the claude-code image
#     booted with a benign `cat` argv so the bridge actually spawns a
#     subprocess, pipes the request body to its stdin, and surfaces the
#     stdout as an A2A artifact. This is the real round-trip the
#     dispatcher does on every ephemeral turn (ADR 0025), minus the
#     dispatcher itself.
#
# What's deferred (best-effort, follow-up tracked in #1099 acceptance):
#   - Path 2 (npm-installed bridge): there is no in-tree image that
#     installs the bridge from npm; building one in CI would need an
#     npm publish step we don't run on every PR. Tracked in
#     https://github.com/cvoya-com/spring-voyage/issues/1120 — when that
#     lands, this script grows a `--path 2` mode.
#   - Path 3 (native A2A, dapr-agent): the dapr image is currently
#     gated behind `SMOKE_DAPR=1` in tests/scripts/smoke-agent-images.sh
#     pending #1110 (the dapr-agents 1.x API change in agents/dapr-agent/
#     /agent.py). When #1110 lands, this script can drop the gate and
#     exercise path 3 directly.
#
# Honors the same DOCKER env var as deployment/build-agent-images.sh.
# Set SMOKE_IMAGE_TAG=<tag> to point at a non-:dev build.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

TAG="${SMOKE_IMAGE_TAG:-dev}"
NAME_SUFFIX="$$"

# ---- runtime selection ---------------------------------------------------
# Mirrors deployment/build-agent-images.sh — docker if reachable, else
# podman. Set DOCKER to force one runtime explicitly.
if [[ -z "${DOCKER:-}" ]]; then
    if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
        DOCKER=docker
    elif command -v podman >/dev/null 2>&1; then
        DOCKER=podman
    else
        echo "::error::neither docker nor podman is on PATH (or docker daemon is unreachable)" >&2
        exit 1
    fi
fi

if ! command -v "${DOCKER}" >/dev/null 2>&1; then
    echo "::error::container CLI '${DOCKER}' not found on PATH" >&2
    exit 1
fi

require() {
    command -v "$1" >/dev/null 2>&1 || {
        echo "::error::required command '$1' not found on PATH" >&2
        exit 1
    }
}
require curl
require jq

log() { printf '[smoke-1087] %s\n' "$*" >&2; }

CONTAINERS=()

cleanup() {
    local rc=$?
    if (( ${#CONTAINERS[@]} > 0 )); then
        for c in "${CONTAINERS[@]}"; do
            "${DOCKER}" rm -f "${c}" >/dev/null 2>&1 || true
        done
    fi
    exit "${rc}"
}
trap cleanup EXIT INT TERM

# Pick a free TCP port on 127.0.0.1.
free_port() {
    python3 - <<'PY'
import socket
with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
    s.bind(("127.0.0.1", 0))
    print(s.getsockname()[1])
PY
}

# Wait until /.well-known/agent.json is reachable on the given URL or fail.
wait_ready() {
    local url="$1" attempt=0 body=""
    for attempt in $(seq 1 30); do
        if body="$(curl -fsS --max-time 2 "${url}" 2>/dev/null)"; then
            printf '%s' "${body}"
            return 0
        fi
        sleep 1
    done
    return 1
}

# ---- 1. Path 1 — claude-code image, A2A message/send round-trip ---------
# We boot the claude-code image with a benign `cat` argv so the bridge
# spawns a real subprocess on `message/send`, pipes the request text to
# its stdin, captures the stdout, and returns it as an A2A artifact.
# This is the same wire round-trip the dispatcher performs on every
# ephemeral turn (ADR 0025).
#
# Using `cat` (not `claude`) keeps the smoke hermetic: no Anthropic API
# key, no network egress, no model. We're verifying the bridge / A2A
# wire path, not Claude itself.
PATH1_IMAGE="localhost/spring-voyage-agent-claude-code:${TAG}"
PATH1_PORT="$(free_port)"
PATH1_NAME="spring-voyage-smoke-1087-path1-${NAME_SUFFIX}"

log "path 1 (claude-code / agent-base bridge): ${PATH1_IMAGE} on :${PATH1_PORT}"

# `["sh","-c","cat"]` is the canonical "echo whatever stdin we get" argv.
# `cat` exits when stdin closes — which the bridge does after writing the
# request text — so the bridge harvests stdout cleanly and the call
# returns instead of hanging.
"${DOCKER}" run -d --rm \
    --name "${PATH1_NAME}" \
    -p "${PATH1_PORT}:8999" \
    -e 'SPRING_AGENT_ARGV=["sh","-c","cat"]' \
    "${PATH1_IMAGE}" >/dev/null
CONTAINERS+=("${PATH1_NAME}")

PATH1_CARD="$(wait_ready "http://127.0.0.1:${PATH1_PORT}/.well-known/agent.json")" || {
    log "::error::path 1: /.well-known/agent.json never returned"
    "${DOCKER}" logs "${PATH1_NAME}" >&2 || true
    exit 1
}

PATH1_PROTO="$(printf '%s' "${PATH1_CARD}" | jq -r '.protocolVersion // empty')"
if [[ "${PATH1_PROTO}" != "0.3" ]]; then
    log "::error::path 1: agent card protocolVersion='${PATH1_PROTO}', expected '0.3'"
    exit 1
fi

# Verify the bridge stamps its version on the response header (per the
# wire contract in ADR 0027). curl -i emits headers; we lower-case for
# matching since HTTP headers are case-insensitive.
PATH1_HEADERS="$(curl -fsS -i "http://127.0.0.1:${PATH1_PORT}/healthz")"
if ! printf '%s\n' "${PATH1_HEADERS}" | tr '[:upper:]' '[:lower:]' | grep -q '^x-spring-voyage-bridge-version: '; then
    log "::error::path 1: response missing x-spring-voyage-bridge-version header"
    printf '%s\n' "${PATH1_HEADERS}" >&2
    exit 1
fi

# Fire a real A2A message/send and assert the bridge spawned `cat` and
# returned its stdout as an artifact.
PROMPT="hello-from-1087-smoke-${NAME_SUFFIX}"
PATH1_RESP="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:${PATH1_PORT}/" \
    -H 'Content-Type: application/json' \
    -d "$(jq -n --arg t "${PROMPT}" '{
        jsonrpc: "2.0",
        id: 1,
        method: "message/send",
        params: {
            message: {
                role: "user",
                parts: [{ text: $t }]
            },
            configuration: { acceptedOutputModes: ["text/plain"] }
        }
    }')" )"

log "path 1 message/send response:"
printf '%s\n' "${PATH1_RESP}" | jq . >&2 || printf '%s\n' "${PATH1_RESP}" >&2

# message/send result is the .NET A2A SDK's SendMessageResponse — a
# field-presence wrapper around `task` or `message`. The bridge always
# returns the AgentTask under `task` (see issue #1115).
PATH1_STATE="$(printf '%s' "${PATH1_RESP}" | jq -r '.result.task.status.state // empty')"
if [[ "${PATH1_STATE}" != "TASK_STATE_COMPLETED" ]]; then
    log "::error::path 1: message/send result.task.status.state='${PATH1_STATE}', expected 'TASK_STATE_COMPLETED'"
    "${DOCKER}" logs "${PATH1_NAME}" >&2 || true
    exit 1
fi

# The bridge surfaces stdout as an artifact; for a `cat` argv the artifact
# text should contain the prompt verbatim. We tolerate trailing whitespace
# differences.
#
# A2A artifacts can live in either result.task.artifacts[].parts[].text
# or result.task.status.message.parts[].text depending on whether the
# bridge attached an error message; we check both and assert the prompt
# shows up somewhere.
PATH1_ARTIFACT="$(printf '%s' "${PATH1_RESP}" | jq -r '
    [
      (.result.task.artifacts // [])[].parts[]?.text,
      (.result.task.status.message.parts // [])[]?.text
    ] | map(select(. != null)) | join("\n")
')"

if [[ "${PATH1_ARTIFACT}" != *"${PROMPT}"* ]]; then
    log "::error::path 1: artifact text did not contain the prompt"
    log "::error::expected to find '${PROMPT}' in:"
    printf '%s\n' "${PATH1_ARTIFACT}" >&2
    exit 1
fi

log "path 1: PASS (protocolVersion=0.3, bridge-version header present, message/send returned TASK_STATE_COMPLETED with echoed prompt)"

"${DOCKER}" rm -f "${PATH1_NAME}" >/dev/null
unset 'CONTAINERS[0]'

# ---- 2. Path 3 — native A2A (dapr-agent) — best-effort -------------------
# Gated behind SMOKE_DAPR=1 because the in-tree dapr image is currently
# blocked by #1110 (dapr-agents 1.x API change). When that issue ships,
# enable this leg by default. tests/scripts/smoke-agent-images.sh shares
# the same gate, so flipping it in one place flips it in both.
PATH3_IMAGE="localhost/spring-voyage-agent-dapr:${TAG}"
if [[ "${SMOKE_DAPR:-0}" == "1" ]]; then
    PATH3_PORT="$(free_port)"
    PATH3_NAME="spring-voyage-smoke-1087-path3-${NAME_SUFFIX}"
    log "path 3 (native A2A / dapr-agent): ${PATH3_IMAGE} on :${PATH3_PORT}"

    "${DOCKER}" run -d --rm \
        --name "${PATH3_NAME}" \
        -p "${PATH3_PORT}:8999" \
        "${PATH3_IMAGE}" >/dev/null
    CONTAINERS+=("${PATH3_NAME}")

    PATH3_CARD="$(wait_ready "http://127.0.0.1:${PATH3_PORT}/.well-known/agent.json")" || {
        log "::error::path 3: /.well-known/agent.json never returned"
        "${DOCKER}" logs "${PATH3_NAME}" >&2 || true
        exit 1
    }
    log "path 3 agent card:"
    printf '%s\n' "${PATH3_CARD}" | jq . >&2

    log "path 3: PASS (agent card reachable; full message/send round-trip skipped pending #1110)"
    "${DOCKER}" rm -f "${PATH3_NAME}" >/dev/null
else
    # TODO(#1110): drop this gate when agents/dapr-agent/agent.py is
    # updated to dapr-agents 1.x. Until then path 3 is exercised only
    # locally with SMOKE_DAPR=1.
    log "path 3: SKIPPED (set SMOKE_DAPR=1 to opt in; tracked by #1110)"
fi

# ---- 3. Path 2 — npm-installed bridge — follow-up ------------------------
# TODO(#1120): exercise path 2 in CI against an in-tree reference image
# that installs @cvoya/spring-voyage-agent-sidecar from npm. Today the
# bridge npm package is published only on agent-base-vX.Y.Z release
# tags, so a per-PR build can't pull it cleanly. #1120 captures the work
# to either publish a `:dev` channel or build an npm tarball from sources
# in CI and `npm install` it inside a throw-away fixture image, so this
# leg can run on every PR.
log "path 2 (npm-installed bridge): SKIPPED (tracked as #1120)"

log "smoke-1087: all enabled paths passed at tag :${TAG}"
