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
#   - Path 2 (npm-installed bridge): a throw-away image built from
#     tests/fixtures/byoi-path2/Dockerfile that `npm i -g`s the bridge
#     from a `npm pack` tarball of the in-tree sidecar sources. End
#     state is identical to the public path-2a recipe in
#     docs/guide/byoi-agent-images.md; same `cat` argv, same A2A
#     round-trip assertion as path 1, just a different image build
#     pedigree. Tracked by #1120.
#
# What's deferred (best-effort, follow-up tracked in #1099 acceptance):
#   - Path 3 (native A2A, dapr-agent): the dapr image is currently
#     gated behind `SMOKE_DAPR=1` in tests/scripts/smoke-agent-images.sh
#     pending #1110 (the dapr-agents 1.x API change in agents/dapr-agent/
#     /agent.py). When #1110 lands, this script can drop the gate and
#     exercise path 3 directly.
#
# Honors the same DOCKER env var as deployment/build-agent-images.sh.
# Set SMOKE_IMAGE_TAG=<tag> to point at a non-:dev path-1 build (path
# 2 always builds a fresh fixture image from in-tree sources).
#
# Modes:
#   --path 1   (default) — path 1 only, plus path 3 when SMOKE_DAPR=1.
#                          Identical to the pre-#1120 behaviour, so
#                          `bash smoke-1087.sh` with no arguments stays
#                          a drop-in replacement for older callers
#                          (CI, contributors' shells, smoke-agent-images.sh).
#   --path 2             — path 2 only.
#   --path all           — paths 1 and 2 (and 3 when SMOKE_DAPR=1).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

TAG="${SMOKE_IMAGE_TAG:-dev}"
NAME_SUFFIX="$$"

# ---- arg parsing ---------------------------------------------------------
# Default `--path 1` keeps existing callers (CI's
# `bash tests/scripts/smoke-1087.sh` invocation in .github/workflows/ci.yml
# `agent-images-smoke`, ad-hoc `smoke-1087.sh` runs in dev shells)
# behaviourally identical to before #1120 landed.
PATH_MODE="1"
while [[ $# -gt 0 ]]; do
    case "$1" in
    --path)
        if [[ $# -lt 2 ]]; then
            echo "::error::--path requires an argument (1, 2, or all)" >&2
            exit 2
        fi
        PATH_MODE="$2"
        shift 2
        ;;
    --path=*)
        PATH_MODE="${1#--path=}"
        shift
        ;;
    -h | --help)
        sed -n '1,/^set -euo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; /^set -euo/d'
        exit 0
        ;;
    *)
        echo "::error::unknown argument: $1 (try --path 1|2|all)" >&2
        exit 2
        ;;
    esac
done

case "${PATH_MODE}" in
1 | 2 | all) ;;
*)
    echo "::error::--path must be one of: 1, 2, all (got: ${PATH_MODE})" >&2
    exit 2
    ;;
esac

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
# Path 2 needs Node + npm to produce the sidecar tarball. Gate the
# probe on the path mode so a path-1-only run on a Node-less host
# still works.
if [[ "${PATH_MODE}" == "2" || "${PATH_MODE}" == "all" ]]; then
    require node
    require npm
fi

log() { printf '[smoke-1087] %s\n' "$*" >&2; }

CONTAINERS=()

# Drop a container name from CONTAINERS once we've already `${DOCKER}
# rm -f`'d it inline, so the EXIT trap doesn't try to remove it again.
# We rebuild the array (`unset CONTAINERS[i]` would leave a sparse
# array which iterates oddly later) and use the `${arr[@]+...}` idiom
# so `set -u` doesn't choke on the empty case.
forget_container() {
    local target="$1" new=() c
    if ((${#CONTAINERS[@]} > 0)); then
        for c in "${CONTAINERS[@]}"; do
            [[ "${c}" != "${target}" ]] && new+=("${c}")
        done
    fi
    CONTAINERS=("${new[@]+"${new[@]}"}")
}
# Image tags + tarball paths produced by path-2 that the trap needs to
# clean up on exit (success or failure). Path 1 reuses an image built
# upstream by deployment/build-agent-images.sh, so it doesn't appear
# here.
PATH2_IMAGES=()
PATH2_TARBALLS=()

cleanup() {
    local rc=$?
    if ((${#CONTAINERS[@]} > 0)); then
        for c in "${CONTAINERS[@]}"; do
            "${DOCKER}" rm -f "${c}" >/dev/null 2>&1 || true
        done
    fi
    if ((${#PATH2_IMAGES[@]} > 0)); then
        for img in "${PATH2_IMAGES[@]}"; do
            "${DOCKER}" rmi -f "${img}" >/dev/null 2>&1 || true
        done
    fi
    if ((${#PATH2_TARBALLS[@]} > 0)); then
        for tgz in "${PATH2_TARBALLS[@]}"; do
            rm -f "${tgz}" 2>/dev/null || true
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
    local url="$1" body=""
    for _ in $(seq 1 30); do
        if body="$(curl -fsS --max-time 2 "${url}" 2>/dev/null)"; then
            printf '%s' "${body}"
            return 0
        fi
        sleep 1
    done
    return 1
}

# ---- shared assertion: A2A message/send round-trip ----------------------
# Both paths share the same wire contract (see ADR 0027), so the same
# assertion shape covers both: agent card has protocolVersion=0.3, the
# bridge stamps x-spring-voyage-bridge-version on responses, and a
# message/send returns status.state="completed" with the prompt echoed
# back in an artifact (because we're feeding it `cat` as the spawned
# CLI). Returns 0 on PASS, dumps logs from the named container and
# returns non-zero on FAIL.
#
# Args: <path-label> <port> <container-name>
assert_a2a_roundtrip() {
    local label="$1" port="$2" container="$3" card proto headers prompt resp state artifact

    card="$(wait_ready "http://127.0.0.1:${port}/.well-known/agent.json")" || {
        log "::error::${label}: /.well-known/agent.json never returned"
        "${DOCKER}" logs "${container}" >&2 || true
        return 1
    }

    proto="$(printf '%s' "${card}" | jq -r '.protocolVersion // empty')"
    if [[ "${proto}" != "0.3" ]]; then
        log "::error::${label}: agent card protocolVersion='${proto}', expected '0.3'"
        return 1
    fi

    # Verify the bridge stamps its version on the response header (per
    # the wire contract in ADR 0027). curl -i emits headers; lower-case
    # for matching since HTTP headers are case-insensitive.
    headers="$(curl -fsS -i "http://127.0.0.1:${port}/healthz")"
    if ! printf '%s\n' "${headers}" | tr '[:upper:]' '[:lower:]' | grep -q '^x-spring-voyage-bridge-version: '; then
        log "::error::${label}: response missing x-spring-voyage-bridge-version header"
        printf '%s\n' "${headers}" >&2
        return 1
    fi

    # Fire a real A2A message/send and assert the bridge spawned `cat`
    # and returned its stdout as an artifact.
    prompt="hello-from-1087-smoke-${NAME_SUFFIX}-${label}"
    resp="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:${port}/" \
        -H 'Content-Type: application/json' \
        -d "$(jq -n --arg t "${prompt}" '{
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
        }')")"

    log "${label} message/send response:"
    printf '%s\n' "${resp}" | jq . >&2 || printf '%s\n' "${resp}" >&2

    # message/send result is the .NET A2A SDK's SendMessageResponse — a
    # field-presence wrapper around `task` or `message`. The bridge
    # always returns the AgentTask under `.result.task`, with the
    # status enum encoded as the proto-style name the .NET SDK pins via
    # [JsonStringEnumMemberName]. See issue #1115 for the rationale and
    # for the bridge change that introduced this shape.
    state="$(printf '%s' "${resp}" | jq -r '.result.task.status.state // empty')"
    if [[ "${state}" != "TASK_STATE_COMPLETED" ]]; then
        log "::error::${label}: message/send result.task.status.state='${state}', expected 'TASK_STATE_COMPLETED'"
        "${DOCKER}" logs "${container}" >&2 || true
        return 1
    fi

    # A2A artifacts can live in either result.task.artifacts[].parts[].text
    # or result.task.status.message.parts[].text depending on whether the
    # bridge attached an error message; check both and assert the prompt
    # shows up somewhere.
    artifact="$(printf '%s' "${resp}" | jq -r '
        [
          (.result.task.artifacts // [])[].parts[]?.text,
          (.result.task.status.message.parts // [])[]?.text
        ] | map(select(. != null)) | join("\n")
    ')"

    if [[ "${artifact}" != *"${prompt}"* ]]; then
        log "::error::${label}: artifact text did not contain the prompt"
        log "::error::expected to find '${prompt}' in:"
        printf '%s\n' "${artifact}" >&2
        return 1
    fi

    log "${label}: PASS (protocolVersion=0.3, bridge-version header present, message/send returned TASK_STATE_COMPLETED with echoed prompt)"
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
run_path1() {
    local image="localhost/spring-voyage-agent-claude-code:${TAG}"
    local port name
    port="$(free_port)"
    name="spring-voyage-smoke-1087-path1-${NAME_SUFFIX}"

    log "path 1 (claude-code / agent-base bridge): ${image} on :${port}"

    # `["sh","-c","cat"]` is the canonical "echo whatever stdin we get"
    # argv. `cat` exits when stdin closes — which the bridge does after
    # writing the request text — so the bridge harvests stdout cleanly
    # and the call returns instead of hanging.
    "${DOCKER}" run -d --rm \
        --name "${name}" \
        -p "${port}:8999" \
        -e 'SPRING_AGENT_ARGV=["sh","-c","cat"]' \
        "${image}" >/dev/null
    CONTAINERS+=("${name}")

    assert_a2a_roundtrip "path 1" "${port}" "${name}" || return 1

    "${DOCKER}" rm -f "${name}" >/dev/null
    forget_container "${name}"
}

# ---- 2. Path 2 — npm-installed bridge, A2A message/send round-trip ------
# Builds a throw-away image from tests/fixtures/byoi-path2/Dockerfile
# that `npm i -g`s the bridge from a `npm pack` tarball of the in-tree
# deployment/agent-sidecar/ sources. The end state inside the
# container is identical to docs/guide/byoi-agent-images.md "Path 2a —
# npm install"; the only difference is that the tarball comes from the
# working tree instead of the npm registry, so a per-PR run doesn't
# need an unpublished `:dev` channel on npmjs.org.
#
# Asserts the same A2A round-trip path 1 does — same `cat` argv, same
# message/send echo. Closes the gap called out in #1120.
run_path2() {
    local sidecar_dir="${REPO_ROOT}/deployment/agent-sidecar"
    local fixture_dir="${REPO_ROOT}/tests/fixtures/byoi-path2"
    local image="localhost/spring-voyage-agent-byoi-path2-smoke:${NAME_SUFFIX}"
    local pkg_version tarball_name tarball_src tarball_dst port name

    log "path 2 (npm-installed bridge): packing in-tree sidecar"

    # `npm pack` writes the tarball into $PWD using the
    # scope-flattened package name + version (e.g.
    # cvoya-spring-voyage-agent-sidecar-1.0.0.tgz). Read the version
    # straight out of package.json so a sidecar version bump doesn't
    # silently leave us building the previous tarball.
    pkg_version="$(jq -r '.version' "${sidecar_dir}/package.json")"
    tarball_name="cvoya-spring-voyage-agent-sidecar-${pkg_version}.tgz"

    # The package's `files` field includes `dist/` but there is no
    # `prepack` script wired up, so a fresh checkout's `npm pack` would
    # ship an empty `dist/`. Run install + build before pack so the
    # tarball is byte-for-byte what `npm publish` would upload.
    (
        cd "${sidecar_dir}"
        npm install --silent --no-audit --no-fund
        npm run --silent build
        # `--pack-destination .` keeps the tarball in $PWD even when
        # newer npm tries to drop it elsewhere; `--silent` keeps the
        # smoke output focused on the assertions.
        rm -f "${tarball_name}"
        npm pack --silent --pack-destination . >/dev/null
    )

    tarball_src="${sidecar_dir}/${tarball_name}"
    tarball_dst="${fixture_dir}/${tarball_name}"

    if [[ ! -f "${tarball_src}" ]]; then
        log "::error::path 2: npm pack did not produce ${tarball_src}"
        return 1
    fi

    cp "${tarball_src}" "${tarball_dst}"
    PATH2_TARBALLS+=("${tarball_src}" "${tarball_dst}")

    log "path 2: building fixture image ${image}"
    "${DOCKER}" build \
        --build-arg "SIDECAR_TARBALL=${tarball_name}" \
        -t "${image}" \
        -f "${fixture_dir}/Dockerfile" \
        "${fixture_dir}" >&2
    PATH2_IMAGES+=("${image}")

    port="$(free_port)"
    name="spring-voyage-smoke-1087-path2-${NAME_SUFFIX}"

    log "path 2 (npm-installed bridge): ${image} on :${port}"

    "${DOCKER}" run -d --rm \
        --name "${name}" \
        -p "${port}:8999" \
        -e 'SPRING_AGENT_ARGV=["sh","-c","cat"]' \
        "${image}" >/dev/null
    CONTAINERS+=("${name}")

    assert_a2a_roundtrip "path 2" "${port}" "${name}" || return 1

    "${DOCKER}" rm -f "${name}" >/dev/null
    forget_container "${name}"
}

# ---- 3. Path 3 — native A2A (dapr-agent) — best-effort -------------------
# Gated behind SMOKE_DAPR=1 because the in-tree dapr image is currently
# blocked by #1110 (dapr-agents 1.x API change). When that issue ships,
# enable this leg by default. tests/scripts/smoke-agent-images.sh shares
# the same gate, so flipping it in one place flips it in both.
run_path3_if_enabled() {
    local image="localhost/spring-voyage-agent-dapr:${TAG}"
    if [[ "${SMOKE_DAPR:-0}" == "1" ]]; then
        local port name card
        port="$(free_port)"
        name="spring-voyage-smoke-1087-path3-${NAME_SUFFIX}"
        log "path 3 (native A2A / dapr-agent): ${image} on :${port}"

        "${DOCKER}" run -d --rm \
            --name "${name}" \
            -p "${port}:8999" \
            "${image}" >/dev/null
        CONTAINERS+=("${name}")

        card="$(wait_ready "http://127.0.0.1:${port}/.well-known/agent.json")" || {
            log "::error::path 3: /.well-known/agent.json never returned"
            "${DOCKER}" logs "${name}" >&2 || true
            return 1
        }
        log "path 3 agent card:"
        printf '%s\n' "${card}" | jq . >&2

        log "path 3: PASS (agent card reachable; full message/send round-trip skipped pending #1110)"
        "${DOCKER}" rm -f "${name}" >/dev/null
        forget_container "${name}"
    else
        # TODO(#1110): drop this gate when agents/dapr-agent/agent.py
        # is updated to dapr-agents 1.x. Until then path 3 is exercised
        # only locally with SMOKE_DAPR=1.
        log "path 3: SKIPPED (set SMOKE_DAPR=1 to opt in; tracked by #1110)"
    fi
}

# ---- driver --------------------------------------------------------------
case "${PATH_MODE}" in
1)
    run_path1
    run_path3_if_enabled
    ;;
2)
    run_path2
    ;;
all)
    run_path1
    run_path2
    run_path3_if_enabled
    ;;
esac

log "smoke-1087: --path ${PATH_MODE} passed at tag :${TAG}"
