#!/usr/bin/env bash
# pool: fast
# ADR-0039 §7 / §9: `--container-runtime` is removed from operator-facing
# unit-create surfaces. The CLI rejects the flag at parse time with a
# migration hint — no API call, no actor side-effect. This scenario pins the
# rejection so a future flag rename (or accidental re-introduction) trips CI
# before any server work is reached.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

name="$(e2e::unit_name reject-cr)"

e2e::log "spring unit create ${name} --top-level --container-runtime podman (legacy flag)"
response="$(e2e::cli unit create "${name}" --top-level --container-runtime podman)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

# Parse-time rejection must surface a non-zero exit code. System.CommandLine
# returns exit 1 for option-validator errors.
if [[ "${code}" != "0" ]]; then
    e2e::ok "legacy --container-runtime rejected at parse time (exit ${code})"
else
    e2e::fail "legacy --container-runtime accepted (exit ${code}); expected non-zero. Body: ${body:0:500}"
fi

# The hint must mention ADR-0039 so operators can find the migration story.
e2e::expect_contains "ADR-0039" "${body}" "rejection message names ADR-0039"
e2e::expect_contains "container runtime" "${body}" "rejection message names the migrated concept"
e2e::expect_contains "platform configuration" "${body}" "rejection message explains the new ownership"

e2e::summary
