#!/usr/bin/env bash
# Spring Voyage — VPS deployment via SSH + Podman.
#
# Ships the deployment/ directory and the spring.env file to a remote host,
# then runs deploy.sh there. The remote host must already have Podman
# installed and the invoking SSH user must be able to run rootless Podman
# (or sudo-less podman) for the target user.
#
# Usage:
#   SPRING_REMOTE_HOST=user@vps ./deploy-remote.sh sync
#   SPRING_REMOTE_HOST=user@vps ./deploy-remote.sh up
#   SPRING_REMOTE_HOST=user@vps ./deploy-remote.sh down
#   SPRING_REMOTE_HOST=user@vps ./deploy-remote.sh logs spring-api
#   SPRING_REMOTE_HOST=user@vps ./deploy-remote.sh deploy   # sync + build + up
#
# Environment:
#   SPRING_REMOTE_HOST      Required. SSH target (user@host or ssh alias).
#   SPRING_REMOTE_DIR       Optional. Remote install dir. Default: /opt/spring-voyage
#   SPRING_REMOTE_SSH_OPTS  Optional. Extra args passed to ssh/rsync (e.g. '-p 2222').
#   SPRING_ENV_FILE         Optional. Local env file to sync. Default: ./spring.env
#   SPRING_SKIP_SOURCE_SYNC Optional. Set to 1 to skip rsyncing the repo (pull images on the VPS instead).
#
# Pull-images-only flow: set SPRING_SKIP_SOURCE_SYNC=1 and configure
# SPRING_PLATFORM_IMAGE / SPRING_AGENT_IMAGE in spring.env to a registry URL.
# The remote deploy.sh will then run the images without rebuilding on the VPS.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

REMOTE_HOST="${SPRING_REMOTE_HOST:-}"
REMOTE_DIR="${SPRING_REMOTE_DIR:-/opt/spring-voyage}"
SSH_OPTS="${SPRING_REMOTE_SSH_OPTS:-}"
ENV_FILE="${SPRING_ENV_FILE:-${SCRIPT_DIR}/spring.env}"
SKIP_SOURCE_SYNC="${SPRING_SKIP_SOURCE_SYNC:-0}"

log() { printf '[deploy-remote] %s\n' "$*" >&2; }
die() { printf '[deploy-remote][error] %s\n' "$*" >&2; exit 1; }

require() {
    command -v "$1" >/dev/null 2>&1 || die "required command '$1' not found on PATH"
}

check_prereqs() {
    require ssh
    require rsync
    [[ -n "${REMOTE_HOST}" ]] || die "SPRING_REMOTE_HOST is required (e.g. user@vps.example.com)"
    [[ -f "${ENV_FILE}" ]] || die "env file not found: ${ENV_FILE} (copy spring.env.example to spring.env)"
}

# shellcheck disable=SC2086
ssh_exec() {
    # $SSH_OPTS intentionally word-split for multi-arg options like '-p 2222'.
    ssh $SSH_OPTS "${REMOTE_HOST}" "$@"
}

# shellcheck disable=SC2086
rsync_to() {
    local src="$1" dst="$2"
    rsync -az --delete \
        ${SSH_OPTS:+-e "ssh ${SSH_OPTS}"} \
        "${src}" "${REMOTE_HOST}:${dst}"
}

remote_mkdir() {
    ssh_exec "mkdir -p '${REMOTE_DIR}' '${REMOTE_DIR}/deployment'"
}

cmd_sync() {
    check_prereqs
    remote_mkdir

    log "syncing deployment/ to ${REMOTE_HOST}:${REMOTE_DIR}/deployment"
    rsync_to "${SCRIPT_DIR}/" "${REMOTE_DIR}/deployment/"

    log "syncing env file (${ENV_FILE}) to ${REMOTE_HOST}:${REMOTE_DIR}/deployment/spring.env"
    rsync_to "${ENV_FILE}" "${REMOTE_DIR}/deployment/spring.env"

    if [[ "${SKIP_SOURCE_SYNC}" != "1" ]]; then
        log "syncing repo sources to ${REMOTE_HOST}:${REMOTE_DIR} (set SPRING_SKIP_SOURCE_SYNC=1 to skip)"
        rsync -az --delete \
            ${SSH_OPTS:+-e "ssh ${SSH_OPTS}"} \
            --exclude '.git/' \
            --exclude 'bin/' --exclude 'obj/' --exclude 'TestResults/' \
            --exclude 'node_modules/' --exclude '.next/' \
            --exclude 'deployment/spring.env' \
            "${REPO_ROOT}/" "${REMOTE_HOST}:${REMOTE_DIR}/"
    fi

    # Ensure scripts are executable remotely (rsync preserves perms but be explicit).
    ssh_exec "chmod +x '${REMOTE_DIR}/deployment/deploy.sh' '${REMOTE_DIR}/deployment/deploy-remote.sh' 2>/dev/null || true"
    log "sync complete"
}

cmd_remote() {
    # Run deploy.sh on the remote with the given arguments.
    check_prereqs
    local args="$*"
    [[ -n "${args}" ]] || die "remote command requires arguments to pass to deploy.sh"
    # shellcheck disable=SC2029
    ssh_exec "cd '${REMOTE_DIR}/deployment' && ./deploy.sh ${args}"
}

cmd_up()      { cmd_remote up; }
cmd_down()    { cmd_remote down; }
cmd_restart() { cmd_remote restart; }
cmd_status()  { cmd_remote status; }
cmd_build()   { cmd_remote build; }

cmd_logs() {
    check_prereqs
    local svc="${1:-}"
    if [[ -n "${svc}" ]]; then
        cmd_remote "logs ${svc}"
    else
        cmd_remote logs
    fi
}

cmd_deploy() {
    cmd_sync
    if [[ "${SKIP_SOURCE_SYNC}" != "1" ]]; then
        cmd_build
    fi
    cmd_up
}

usage() {
    cat <<EOF
Spring Voyage — remote (VPS) Podman deployment

Commands:
  sync            rsync deployment/ and env file to the remote
  deploy          sync + build (if syncing sources) + up
  up              start the remote stack
  down            stop the remote stack
  restart         down + up remotely
  status          show remote container status
  build           build images on the remote
  logs [service]  follow remote logs

Environment:
  SPRING_REMOTE_HOST       required, e.g. user@vps.example.com
  SPRING_REMOTE_DIR        remote install dir (default: /opt/spring-voyage)
  SPRING_REMOTE_SSH_OPTS   extra ssh/rsync args (e.g. '-p 2222')
  SPRING_ENV_FILE          local env file (default: ./spring.env)
  SPRING_SKIP_SOURCE_SYNC  set to 1 to skip rsyncing sources (pull images instead)
EOF
}

main() {
    local cmd="${1:-}"
    shift || true
    case "${cmd}" in
        sync)                cmd_sync ;;
        deploy)              cmd_deploy ;;
        up)                  cmd_up ;;
        down)                cmd_down ;;
        restart)             cmd_restart ;;
        status)              cmd_status ;;
        build)               cmd_build ;;
        logs)                cmd_logs "$@" ;;
        ""|-h|--help|help)   usage ;;
        *)                   usage; exit 2 ;;
    esac
}

main "$@"
