#!/usr/bin/env bash
# Spring Voyage — resync the container clock with the host (temporary workaround).
#
# TEMPORARY WORKAROUND for issue #2595. Delete this script once the
# permanent fix lands.
#
# Why this exists:
#
#   On macOS / Windows, Podman runs containers inside a libkrun/QEMU VM
#   (the "podman machine"). When the host sleeps, the VM's clock freezes
#   and does NOT resync on resume — it falls behind real time by however
#   long the host was asleep. Every container inherits that skewed clock.
#
#   The GitHub connector signs its GitHub App JWT with the container's
#   local clock (GitHubAuth.GenerateJwt). A skewed clock produces a JWT
#   whose `exp` is already in the past, so GitHub rejects every App API
#   call with the generic message "Bad credentials" — surfaced in the
#   unit wizard as "Failed to list GitHub repositories Bad credentials".
#
#   The fix is simply to step the podman-machine VM clock back onto NTP
#   time. Containers share the VM kernel clock, so no container restart
#   is needed.
#
# Usage:
#   cd eng/deploy/
#   ./resync-container-clock.sh           # detect skew, resync if needed
#   ./resync-container-clock.sh --check   # report skew only, change nothing
#
# Preconditions:
#   - podman on PATH.
#   - A running `podman machine` (macOS/Windows). On native Linux there is
#     no VM, containers share the host clock, and this script is a no-op.

set -euo pipefail

CHECK_ONLY=0
# Skew (seconds) above which we consider the clock broken for App JWTs.
# GitHub tolerates only ~60s of drift; 30s leaves a safety margin.
SKEW_THRESHOLD=30

log() { printf '[resync-container-clock] %s\n' "$*" >&2; }
die() { printf '[resync-container-clock][error] %s\n' "$*" >&2; exit 1; }

usage() {
    cat >&2 <<'USAGE'
Usage: resync-container-clock.sh [--check]

Options:
  --check       Report the host/VM clock skew and exit without changing it.
  -h, --help    Show this help and exit.
USAGE
}

while (( $# > 0 )); do
    case "$1" in
        --check)   CHECK_ONLY=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *)         usage; die "unknown argument: $1" ;;
    esac
done

command -v podman >/dev/null 2>&1 || die "podman not found on PATH."

# No podman machine => native Linux, containers share the host clock.
if ! podman machine list --format '{{.Name}}' 2>/dev/null | grep -q .; then
    log "No podman machine found — containers share the host clock; nothing to do."
    exit 0
fi

host_epoch="$(date -u +%s)"
vm_epoch="$(podman machine ssh 'date -u +%s' 2>/dev/null)" \
    || die "could not read the podman-machine clock (is the machine running?)."

skew=$(( host_epoch - vm_epoch ))
abs_skew=${skew#-}

log "host epoch ${host_epoch}, podman-machine epoch ${vm_epoch}, skew ${skew}s."

if (( abs_skew <= SKEW_THRESHOLD )); then
    log "Clock skew is within ${SKEW_THRESHOLD}s — no resync needed."
    exit 0
fi

if (( CHECK_ONLY == 1 )); then
    log "Clock is skewed by ${abs_skew}s. Re-run without --check to resync."
    exit 1
fi

log "Clock skewed by ${abs_skew}s — stepping the podman-machine clock onto NTP time."

# chronyd runs inside the podman machine; `makestep` forces an immediate
# one-shot correction instead of slewing slowly.
podman machine ssh 'sudo chronyc makestep' >/dev/null 2>&1 \
    || die "chronyc makestep failed inside the podman machine."

# Give chrony a moment to step, then re-measure.
sleep 2
new_vm_epoch="$(podman machine ssh 'date -u +%s' 2>/dev/null)" \
    || die "could not re-read the podman-machine clock after resync."
new_host_epoch="$(date -u +%s)"
new_skew=$(( new_host_epoch - new_vm_epoch ))
new_abs_skew=${new_skew#-}

if (( new_abs_skew > SKEW_THRESHOLD )); then
    die "clock still skewed by ${new_abs_skew}s after resync — check chronyd in the podman machine."
fi

log "Resynced — skew is now ${new_skew}s. Click 'Recheck installations' in the unit wizard."
