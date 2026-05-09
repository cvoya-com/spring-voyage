#!/usr/bin/env bash
# pool: fast
# ADR-0039 sections 7 / 9: `--container-runtime` is removed from operator-facing
# agent-create surfaces. The CLI rejects the flag at parse time with a
# migration hint on stderr; no API call, no agent side-effect.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

name="$(e2e::agent_name reject-cr)"
stdout_file="$(mktemp)"
stderr_file="$(mktemp)"
trap 'rm -f "${stdout_file}" "${stderr_file}"' EXIT

e2e::log "spring agent create --name ${name} --container-runtime docker (legacy flag)"
set +e
# SPRING_CLI is intentionally word-split: it may be a single binary path
# or a multi-token command such as "dotnet run --project <path> --".
# shellcheck disable=SC2086
${SPRING_CLI} agent create --name "${name}" --container-runtime docker >"${stdout_file}" 2>"${stderr_file}"
code=$?
set -e

stderr="$(<"${stderr_file}")"
stdout="$(<"${stdout_file}")"

if [[ "${code}" != "0" ]]; then
    e2e::ok "legacy --container-runtime rejected at parse time (exit ${code})"
else
    e2e::fail "legacy --container-runtime accepted (exit ${code}); expected non-zero. Stdout: ${stdout:0:500}"
fi

e2e::expect_contains "ADR-0039" "${stderr}" "stderr rejection message names ADR-0039"
e2e::expect_contains "container runtime" "${stderr}" "stderr rejection message names the migrated concept"
e2e::expect_contains "platform configuration" "${stderr}" "stderr rejection message explains the new ownership"

e2e::summary
