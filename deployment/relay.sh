#!/usr/bin/env bash
# Spring Voyage — webhook relay (SSH reverse tunnel for local development).
#
# Binds a public port on a remote relay host (typically a small VPS) to a
# webhook endpoint listening on this local machine, so third-party webhook
# providers (GitHub, etc.) can deliver events to a developer laptop that
# has no public IP.
#
# How it works:
#
#   provider ----> https://$RELAY_HOST:$RELAY_REMOTE_PORT
#                       |
#                       | (SSH reverse tunnel opened by this script)
#                       v
#                  localhost:$LOCAL_WEBHOOK_PORT on the dev machine
#                       |
#                       v
#                  spring-api (or `dotnet run` on the dev machine)
#
# The relay host only needs:
#   1. sshd configured to allow remote port forwarding (the default) and to
#      bind forwarded ports on an externally reachable interface. Set
#      "GatewayPorts clientspecified" (or "yes") in /etc/ssh/sshd_config.
#   2. A TLS terminator (Caddy, nginx) fronting $RELAY_REMOTE_PORT if the
#      webhook provider requires HTTPS. Typical setup: run deploy.sh on the
#      VPS with a dedicated WEBHOOK_HOSTNAME that proxies to the tunneled
#      port on localhost.
#
# This script is for local dev only. Production webhooks should hit a
# publicly deployed API directly.
#
# Environment variables:
#
#   RELAY_HOST          Required. Hostname or IP of the relay VPS.
#   RELAY_USER          Optional. SSH user on the relay. Default: current user.
#   RELAY_SSH_PORT      Optional. SSH port on the relay. Default: 22.
#   RELAY_REMOTE_PORT   Optional. Public port on the relay to bind.
#                       Default: 19080.
#   RELAY_BIND_ADDRESS  Optional. Interface on the relay to bind the
#                       forwarded port. Use "0.0.0.0" to expose publicly
#                       (requires GatewayPorts). Default: 127.0.0.1 (safe
#                       default — front it with a local reverse proxy).
#   LOCAL_WEBHOOK_PORT  Optional. Local port where the webhook endpoint
#                       listens. Default: 8080.
#   LOCAL_WEBHOOK_HOST  Optional. Local interface. Default: 127.0.0.1.
#   RELAY_SSH_KEY       Optional. Path to an SSH identity file.
#   RELAY_EXTRA_SSH_OPTS Optional. Additional args appended to ssh.
#   RELAY_RECONNECT_DELAY Optional. Seconds to wait between reconnects
#                         after an abnormal exit. Default: 5.
#
# Usage:
#
#   export RELAY_HOST=relay.example.com
#   export RELAY_USER=webhooks
#   export RELAY_REMOTE_PORT=19080
#   export LOCAL_WEBHOOK_PORT=8080
#   ./relay.sh
#
# Stop with Ctrl-C; the reconnect loop exits cleanly on SIGINT/SIGTERM.
#
# Uses autossh(1) when available for automatic reconnection; falls back to
# plain ssh in a bash reconnect loop otherwise.

set -euo pipefail

RELAY_HOST="${RELAY_HOST:-}"
RELAY_USER="${RELAY_USER:-${USER:-}}"
RELAY_SSH_PORT="${RELAY_SSH_PORT:-22}"
RELAY_REMOTE_PORT="${RELAY_REMOTE_PORT:-19080}"
RELAY_BIND_ADDRESS="${RELAY_BIND_ADDRESS:-127.0.0.1}"
LOCAL_WEBHOOK_PORT="${LOCAL_WEBHOOK_PORT:-8080}"
LOCAL_WEBHOOK_HOST="${LOCAL_WEBHOOK_HOST:-127.0.0.1}"
RELAY_SSH_KEY="${RELAY_SSH_KEY:-}"
RELAY_EXTRA_SSH_OPTS="${RELAY_EXTRA_SSH_OPTS:-}"
RELAY_RECONNECT_DELAY="${RELAY_RECONNECT_DELAY:-5}"

log()  { printf '[relay] %s\n' "$*" >&2; }
die()  { printf '[relay][error] %s\n' "$*" >&2; exit 1; }

require_positive_int() {
    local name="$1" value="$2"
    [[ "${value}" =~ ^[0-9]+$ ]] || die "${name} must be a positive integer (got '${value}')"
    (( value > 0 )) || die "${name} must be > 0 (got '${value}')"
}

check_config() {
    [[ -n "${RELAY_HOST}" ]] || die "RELAY_HOST is required (e.g. export RELAY_HOST=relay.example.com)"
    [[ -n "${RELAY_USER}" ]] || die "RELAY_USER is required (could not determine current user)"
    require_positive_int RELAY_SSH_PORT "${RELAY_SSH_PORT}"
    require_positive_int RELAY_REMOTE_PORT "${RELAY_REMOTE_PORT}"
    require_positive_int LOCAL_WEBHOOK_PORT "${LOCAL_WEBHOOK_PORT}"
    require_positive_int RELAY_RECONNECT_DELAY "${RELAY_RECONNECT_DELAY}"
}

build_ssh_opts() {
    # Echo a NUL-separated list of ssh args on stdout so the caller can read
    # them into an array safely. Using stdout avoids depending on bash name
    # references, which keeps the script portable across bash 3.x (macOS).
    local opts=(
        -N
        -p "${RELAY_SSH_PORT}"
        -o ExitOnForwardFailure=yes
        -o ServerAliveInterval=30
        -o ServerAliveCountMax=3
        -o StrictHostKeyChecking=accept-new
        -R "${RELAY_BIND_ADDRESS}:${RELAY_REMOTE_PORT}:${LOCAL_WEBHOOK_HOST}:${LOCAL_WEBHOOK_PORT}"
    )
    if [[ -n "${RELAY_SSH_KEY}" ]]; then
        [[ -f "${RELAY_SSH_KEY}" ]] || die "RELAY_SSH_KEY file not found: ${RELAY_SSH_KEY}"
        opts+=( -i "${RELAY_SSH_KEY}" )
    fi
    if [[ -n "${RELAY_EXTRA_SSH_OPTS}" ]]; then
        # shellcheck disable=SC2206  # intentional word-split for user-supplied opts
        local extra=( ${RELAY_EXTRA_SSH_OPTS} )
        opts+=( "${extra[@]}" )
    fi
    opts+=( "${RELAY_USER}@${RELAY_HOST}" )
    local opt
    for opt in "${opts[@]}"; do
        printf '%s\0' "${opt}"
    done
}

TUNNEL_PID=""

on_exit() {
    if [[ -n "${TUNNEL_PID}" ]] && kill -0 "${TUNNEL_PID}" 2>/dev/null; then
        log "shutting down tunnel (pid ${TUNNEL_PID})"
        kill "${TUNNEL_PID}" 2>/dev/null || true
        wait "${TUNNEL_PID}" 2>/dev/null || true
    fi
    log "relay stopped"
}

on_signal() {
    log "received termination signal, stopping relay"
    trap - EXIT
    on_exit
    exit 0
}

run_autossh() {
    # autossh takes the same args as ssh plus AUTOSSH_* env vars.
    local -a ssh_opts=()
    local opt
    while IFS= read -r -d '' opt; do
        ssh_opts+=( "${opt}" )
    done < <(build_ssh_opts)

    log "using autossh for supervised reconnect"
    # AUTOSSH_GATETIME=0 disables the initial connection settling delay so
    # autossh restarts the ssh process after any failure, including the
    # first one. Useful when the relay is flaky on startup.
    AUTOSSH_GATETIME=0 autossh -M 0 "${ssh_opts[@]}" &
    TUNNEL_PID=$!
    # Block on the child so signal traps fire.
    wait "${TUNNEL_PID}"
    local rc=$?
    TUNNEL_PID=""
    return "${rc}"
}

run_plain_ssh_loop() {
    log "autossh not found on PATH — using plain ssh with a reconnect loop"
    while true; do
        local -a ssh_opts=()
        local opt
        while IFS= read -r -d '' opt; do
            ssh_opts+=( "${opt}" )
        done < <(build_ssh_opts)

        log "opening reverse tunnel ${RELAY_BIND_ADDRESS}:${RELAY_REMOTE_PORT} -> ${LOCAL_WEBHOOK_HOST}:${LOCAL_WEBHOOK_PORT} via ${RELAY_USER}@${RELAY_HOST}:${RELAY_SSH_PORT}"
        ssh "${ssh_opts[@]}" &
        TUNNEL_PID=$!
        local rc=0
        wait "${TUNNEL_PID}" || rc=$?
        TUNNEL_PID=""

        if (( rc == 0 )); then
            log "ssh exited cleanly (rc=0); not reconnecting"
            return 0
        fi

        log "ssh exited with rc=${rc}; reconnecting in ${RELAY_RECONNECT_DELAY}s"
        sleep "${RELAY_RECONNECT_DELAY}"
    done
}

main() {
    check_config
    trap on_signal INT TERM
    trap on_exit EXIT

    log "relay target: ${RELAY_USER}@${RELAY_HOST}:${RELAY_SSH_PORT}"
    log "forwarding remote ${RELAY_BIND_ADDRESS}:${RELAY_REMOTE_PORT} -> local ${LOCAL_WEBHOOK_HOST}:${LOCAL_WEBHOOK_PORT}"

    if command -v autossh >/dev/null 2>&1; then
        run_autossh
    else
        run_plain_ssh_loop
    fi
}

main "$@"
