#!/usr/bin/env bash
# Verify that a unit created with the default omnibus image
# (ghcr.io/cvoya-com/spring-voyage-agents:latest) passes creation and
# registers the image field correctly.
#
# #1514 — spring-voyage-agents omnibus image.
#
# SKIP CONDITION (intentional, not a defect):
#   The omnibus image does not exist in GHCR until the first
#   `release-spring-voyage-agents.yml` publish run completes. This scenario
#   guards against that by checking whether the image is pullable before
#   asserting on image-related behaviour.  When the image is absent the
#   scenario prints a clear skip message and exits 0 — callers should NOT
#   treat a skip as a failure.  Once the image is published this scenario
#   will run for real on every CI invocation.
#
# What this test exercises:
#   1. `spring unit create --image ghcr.io/cvoya-com/spring-voyage-agents:latest`
#      exits 0 and returns a unit with the correct image field.
#   2. `spring unit status <name>` confirms the image field is persisted.
#   3. `docker pull ghcr.io/cvoya-com/spring-voyage-agents:latest` succeeds
#      (acceptance criterion from #1514) — but only when the image exists.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

OMNIBUS_IMAGE="ghcr.io/cvoya-com/spring-voyage-agents:latest"

# ---- skip guard ----------------------------------------------------------
# Detect whether the image is available at the registry. We use docker
# manifest inspect (no actual pull) so the check is fast. Requires
# `docker` or `DOCKER_CLI` on PATH; when neither is present we still skip
# rather than fail — the test runner environment may not have a daemon.
_docker_cmd=""
if command -v docker >/dev/null 2>&1; then
    _docker_cmd=docker
elif [[ -n "${DOCKER_CLI:-}" ]] && command -v "${DOCKER_CLI}" >/dev/null 2>&1; then
    _docker_cmd="${DOCKER_CLI}"
fi

_image_available=0
if [[ -n "${_docker_cmd}" ]]; then
    if "${_docker_cmd}" manifest inspect "${OMNIBUS_IMAGE}" >/dev/null 2>&1; then
        _image_available=1
    fi
fi

if [[ "${_image_available}" -eq 0 ]]; then
    e2e::log "SKIP: ${OMNIBUS_IMAGE} is not yet published to GHCR."
    e2e::log "  This is expected on the first run after #1514 merges."
    e2e::log "  Once the release-spring-voyage-agents workflow publishes"
    e2e::log "  the image, this scenario will run for real."
    # Exit 0 intentionally — the skip is not a test failure.
    exit 0
fi

# ---- test body -----------------------------------------------------------
name="$(e2e::unit_name omnibus-img)"
trap 'e2e::cleanup_unit "${name}"' EXIT

# 1. Create a unit with the omnibus image.
e2e::log "spring unit create ${name} --image ${OMNIBUS_IMAGE}"
response="$(e2e::cli_unit_create --output json "${name}" --image "${OMNIBUS_IMAGE}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"
e2e::expect_status "0" "${code}" "unit create with omnibus image succeeds"
e2e::expect_contains "\"name\": \"${name}\"" "${body}" "create response carries the unit name"
e2e::expect_contains "\"image\": \"${OMNIBUS_IMAGE}\"" "${body}" "create response carries the omnibus image"

# 2. Confirm the image field is persisted after creation.
e2e::log "spring unit status ${name}"
status_response="$(e2e::cli --output json unit status "${name}")"
status_code="${status_response##*$'\n'}"
status_body="${status_response%$'\n'*}"
e2e::expect_status "0" "${status_code}" "unit status after omnibus-image creation succeeds"
e2e::expect_contains "\"image\": \"${OMNIBUS_IMAGE}\"" "${status_body}" "unit status shows omnibus image persisted"

# 3. Confirm docker pull succeeds (acceptance criterion from #1514).
if [[ -n "${_docker_cmd}" ]]; then
    e2e::log "docker pull ${OMNIBUS_IMAGE}"
    if "${_docker_cmd}" pull "${OMNIBUS_IMAGE}" >/dev/null 2>&1; then
        e2e::ok "docker pull ${OMNIBUS_IMAGE} succeeded"
    else
        e2e::fail "docker pull ${OMNIBUS_IMAGE} failed — image may not be published yet"
    fi
else
    e2e::log "docker not available; skipping docker pull check"
fi

e2e::summary
