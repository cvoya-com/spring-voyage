#!/usr/bin/env bash
# pool: fast
# ADR-0039 sections 7 / 9: `--container-runtime` is removed from operator-facing
# agent-create surfaces. The CLI rejects the flag at parse time with a
# migration hint in the merged CLI output; no API call, no agent side-effect.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

name="$(e2e::agent_name reject-cr)"

e2e::log "spring agent create --name ${name} --container-runtime podman (legacy flag)"
response="$(e2e::cli agent create --name "${name}" --container-runtime podman)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

if [[ "${code}" != "0" ]]; then
    e2e::ok "legacy --container-runtime rejected at parse time (exit ${code})"
else
    e2e::fail "legacy --container-runtime accepted (exit ${code}); expected non-zero. Body: ${body:0:500}"
fi

expected="--container-runtime was removed in ADR-0039. The container runtime is "
expected+="platform configuration — the host process picks one runtime at deploy "
expected+="time and every agent on that host uses it. See ADR-0039 §7."
e2e::expect_contains "${expected}" "${body}" "rejection message prints the ADR-0039 migration hint"

e2e::summary
