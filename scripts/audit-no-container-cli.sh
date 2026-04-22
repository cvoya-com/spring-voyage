#!/usr/bin/env bash
# Audit: no worker-side podman/docker CLI invocations.
#
# Stage 2 of #522 / #1063 routed every container operation through the
# host-process spring-dispatcher. The worker container itself MUST NOT
# hold a podman/docker binding any more — every shellout to those
# binaries inside src/ outside the dispatcher (and outside
# ProcessContainerRuntime, which is what the dispatcher itself uses)
# is a regression.
#
# This script is invoked by the .github/workflows/ci.yml
# `audit-no-container-cli` job on every PR. It greps for the four
# patterns that historically appeared in worker-side code:
#
#   - "podman " / "docker " as a literal command prefix
#   - Process.Start with "podman" / "docker" arguments
#   - new ProcessStartInfo(... "podman" / "docker" ...)
#   - "podman exec" / "docker exec" sidecar-health pattern
#
# The allowlist is deliberately tight:
#
#   - src/Cvoya.Spring.Dapr/Execution/ProcessContainerRuntime.cs
#     (the only CLI shellout site, run by the dispatcher process)
#   - src/Cvoya.Spring.Dispatcher/**
#     (the dispatcher's own DI / startup probes)
#   - tests/**
#     (test harnesses that mock the runtime)
#
# Anything else matching a container-CLI pattern fails the audit.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${REPO_ROOT}"

SCAN_PATHS=(src)
ALLOWED_PATHS=(
    "src/Cvoya.Spring.Dapr/Execution/ProcessContainerRuntime.cs"
    "src/Cvoya.Spring.Dispatcher/"
)

# Patterns we consider "worker-side calls a container CLI". Each pattern
# is an extended-regex that ripgrep / grep -E understands.
PATTERNS=(
    'Process\.Start[^A-Za-z0-9_]*\(.*"(podman|docker)"'
    'ProcessStartInfo[[:space:]]*\([[:space:]]*"(podman|docker)"'
    'FileName[[:space:]]*=[[:space:]]*"(podman|docker)"'
    '"(podman|docker)[[:space:]]+(run|start|stop|rm|network|exec|pull|inspect|logs|create|kill)"'
)

violations=0
tmp_matches="$(mktemp)"
trap 'rm -f "${tmp_matches}"' EXIT

# Prefer ripgrep — falls back to grep -rE for portability with the dev
# bash on macOS where rg may not be on PATH.
if command -v rg >/dev/null 2>&1; then
    grep_cmd=(rg --no-heading --line-number --color=never -e)
else
    grep_cmd=(grep -RnE --color=never -e)
fi

for pat in "${PATTERNS[@]}"; do
    : >"${tmp_matches}"
    "${grep_cmd[@]}" "${pat}" "${SCAN_PATHS[@]}" >"${tmp_matches}" 2>/dev/null || true

    while IFS= read -r line; do
        [[ -z "${line}" ]] && continue
        # ripgrep emits "path:line:match", grep -RnE emits the same.
        path="${line%%:*}"

        allowed=0
        for allow in "${ALLOWED_PATHS[@]}"; do
            # Allow either an exact-file allowlist entry or a directory
            # prefix (entries ending in '/').
            if [[ "${allow}" == */ && "${path}" == "${allow}"* ]]; then
                allowed=1
                break
            fi
            if [[ "${path}" == "${allow}" ]]; then
                allowed=1
                break
            fi
        done

        if [[ ${allowed} -eq 0 ]]; then
            echo "[audit] worker-side container CLI invocation: ${line}"
            violations=$((violations + 1))
        fi
    done <"${tmp_matches}"
done

if [[ ${violations} -gt 0 ]]; then
    cat >&2 <<EOF
[audit] found ${violations} worker-side container CLI invocation(s).

Stage 2 of #522 / #1063 made the host-process spring-dispatcher the
sole owner of podman/docker. Worker-side code must call
IContainerRuntime instead — DispatcherClientContainerRuntime forwards
every operation to the dispatcher over HTTP.

If this is a legitimate dispatcher-only addition, add the path to the
ALLOWED_PATHS array in scripts/audit-no-container-cli.sh.
EOF
    exit 1
fi

echo "[audit] no worker-side podman/docker CLI invocations found."
