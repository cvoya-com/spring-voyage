#!/usr/bin/env bash
# Spring Voyage — local-dev Slack-events forwarder via Socket Mode.
#
# Bridges Slack Socket Mode (a WebSocket Slack pushes events / slash
# commands / interactions over) to a locally-running Spring Voyage API.
# This is the Slack equivalent of `gh-webhook-forward.sh`: it lets an
# operator receive Slack deliveries on `http://localhost` without
# provisioning a public HTTPS tunnel.
#
# How it works:
#
#   slack.com  ----> WebSocket (Socket Mode; outbound from the bridge)
#                       |
#                       | spring connector slack forward
#                       v
#                  HTTP POST to http://localhost/api/v1/tenant/connectors/slack/{events,commands,interactions}
#                       |
#                       v
#                  spring-api on the dev machine
#
# Preconditions:
#
#   1. The Slack app must have Socket Mode enabled in its manifest. Use
#         spring connector slack install --socket-mode
#      when first registering the app (issue #2868).
#   2. An app-level (xapp-…) token with the `connections:write` scope.
#      Generate one from https://api.slack.com/apps/<APP_ID>/general →
#      "App-Level Tokens".
#   3. `eng/config/spring.env` must contain:
#         Slack__SocketMode__AppToken=xapp-…
#         Slack__OAuth__SigningSecret=…   (the secret Slack issued at install)
#      `Slack__OAuth__SigningSecret` is populated automatically by
#      `spring connector slack install --write-env`; the app-level token
#      is operator-paste once (Slack does not expose it via the Manifest
#      API).
#
# Usage:
#
#   ./slack-events-forward.sh
#   ./slack-events-forward.sh --target http://localhost:5000
#   ./slack-events-forward.sh --env /custom/path/spring.env
#
# Stop with Ctrl-C.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
CONFIG_DIR="${REPO_ROOT}/eng/config"

TARGET="${SV_SLACK_TARGET:-http://localhost}"
ENV_FILE="${SV_SLACK_ENV_FILE:-${CONFIG_DIR}/spring.env}"
CLI_PROJECT="${SV_SLACK_CLI_PROJECT:-${REPO_ROOT}/src/Cvoya.Spring.Cli}"

if [[ -t 2 && -z "${NO_COLOR:-}" ]]; then
    C_RED=$'\033[31m'
    C_GREEN=$'\033[32m'
    C_YELLOW=$'\033[33m'
    C_CYAN=$'\033[36m'
    C_RESET=$'\033[0m'
else
    C_RED='' C_GREEN='' C_YELLOW='' C_CYAN='' C_RESET=''
fi

log()      { printf '[slack-events-forward] %s\n' "$*" >&2; }
log_warn() { printf '%s[slack-events-forward] %s%s\n' "${C_YELLOW}" "$*" "${C_RESET}" >&2; }
log_err()  { printf '%s[slack-events-forward] %s%s\n' "${C_RED}"    "$*" "${C_RESET}" >&2; }
log_info() { printf '%s[slack-events-forward] %s%s\n' "${C_CYAN}"   "$*" "${C_RESET}" >&2; }
log_ok()   { printf '%s[slack-events-forward] %s%s\n' "${C_GREEN}"  "$*" "${C_RESET}" >&2; }
die()      { log_err "error: $*"; exit 1; }

usage() {
    cat >&2 <<'USAGE'
Usage: slack-events-forward.sh [--target URL] [--env PATH]

Optional:
  --target URL    Local Spring Voyage API base URL to deliver to.
                  Default: http://localhost
                  (or $SV_SLACK_TARGET).
  --env PATH      Path to the env file holding Slack__SocketMode__AppToken
                  and Slack__OAuth__SigningSecret.
                  Default: <repo>/eng/config/spring.env
                  (or $SV_SLACK_ENV_FILE).
  -h, --help      Show this help and exit.
USAGE
}

while (( $# > 0 )); do
    case "$1" in
        --target)
            [[ $# -ge 2 ]] || die "--target requires a value"
            TARGET="$2"
            shift 2
            ;;
        --target=*)
            TARGET="${1#--target=}"
            shift
            ;;
        --env)
            [[ $# -ge 2 ]] || die "--env requires a path"
            ENV_FILE="$2"
            shift 2
            ;;
        --env=*)
            ENV_FILE="${1#--env=}"
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            usage
            die "unknown argument: $1"
            ;;
    esac
done

# --- Preflight ---------------------------------------------------------------

command -v dotnet >/dev/null 2>&1 \
    || die "dotnet CLI not found on PATH. Install .NET 10 — https://dotnet.microsoft.com/."

[[ -d "${CLI_PROJECT}" ]] \
    || die "Cvoya.Spring.Cli project not found at ${CLI_PROJECT}.
       Run this script from a Spring Voyage checkout, or set
       \$SV_SLACK_CLI_PROJECT to point at the CLI project directory."

[[ -f "${ENV_FILE}" ]] \
    || die "env file not found: ${ENV_FILE}
       Run eng/deploy/setup.sh first, or pass --env <path> to point at a different file."

# Read Slack__SocketMode__AppToken + Slack__OAuth__SigningSecret without
# sourcing the file. The env file may contain other shell-hostile values
# (single-quoted PEMs etc.); a stray expansion would break this script.
extract_key() {
    local key="$1"
    awk -v key="$key" -F= '
        /^[[:space:]]*#/        { next }
        /^[[:space:]]*$/        { next }
        {
            sub(/^[[:space:]]+/, "", $1)
            sub(/[[:space:]]+$/, "", $1)
            if ($1 != key) { next }
            sub(/^[^=]*=/, "", $0)
            gsub(/^[[:space:]]+|[[:space:]]+$/, "", $0)
            if ($0 ~ /^".*"$/ || $0 ~ /^'\''.*'\''$/) {
                $0 = substr($0, 2, length($0) - 2)
            }
            print $0
            exit
        }
    ' "${ENV_FILE}"
}

APP_TOKEN="$(extract_key Slack__SocketMode__AppToken)"
SIGNING_SECRET="$(extract_key Slack__OAuth__SigningSecret)"

[[ -n "${APP_TOKEN}" ]] \
    || die "Slack__SocketMode__AppToken is not set in ${ENV_FILE}.
       Generate an app-level token (xapp-…) with the 'connections:write'
       scope from https://api.slack.com/apps → your app → 'App-Level
       Tokens', then add the line:
           Slack__SocketMode__AppToken=xapp-…
       to ${ENV_FILE}."

[[ "${APP_TOKEN}" == xapp-* ]] \
    || log_warn "Slack__SocketMode__AppToken does not start with 'xapp-' — Slack will reject the connection."

[[ -n "${SIGNING_SECRET}" ]] \
    || die "Slack__OAuth__SigningSecret is not set in ${ENV_FILE}.
       Run 'spring connector slack install --write-env --socket-mode'
       so the secret Slack issues at app creation is captured."

# --- Forward -----------------------------------------------------------------

log "bridging Slack Socket Mode → ${TARGET}"
log "stop with Ctrl-C"

forward_args=(
    run --project "${CLI_PROJECT}" --
    connector slack forward
    --app-token "${APP_TOKEN}"
    --signing-secret "${SIGNING_SECRET}"
    --target "${TARGET}"
)

RETRY_DELAY=5

# Filter for the CLI subcommand's combined stdout+stderr. Promotes the
# events to one-line summaries with color so the operator can tell at a
# glance what's flowing.
filter_forward_output() {
    awk \
        -v C_GREEN="${C_GREEN}" \
        -v C_YELLOW="${C_YELLOW}" \
        -v C_RED="${C_RED}" \
        -v C_RESET="${C_RESET}" '
    /^\[forward\] connected/      { printf "%s%s%s\n", C_GREEN,  $0, C_RESET; fflush(); next }
    /^\[forward\] reconnect/      { printf "%s%s%s\n", C_YELLOW, $0, C_RESET; fflush(); next }
    /^\[forward\] disconnect/     { printf "%s%s%s\n", C_RED,    $0, C_RESET; fflush(); next }
    /^\[forward\] events_api/     { printf "%s%s%s\n", C_GREEN,  $0, C_RESET; fflush(); next }
    /^\[forward\] slash_command/  { printf "%s%s%s\n", C_GREEN,  $0, C_RESET; fflush(); next }
    /^\[forward\] interactive/    { printf "%s%s%s\n", C_GREEN,  $0, C_RESET; fflush(); next }
    /^[[:space:]]*$/              { next }
    { print; fflush() }
    '
}

# The subcommand is responsible for reconnecting on its own (see
# SocketModeBridge.RunAsync). Wrap it in a retry loop so a hard CLI crash
# also recovers rather than aborting the dev session.
while true; do
    { dotnet "${forward_args[@]}" 2>&1 | filter_forward_output >&2; } || true
    if [[ -n "${SV_SLACK_NO_RETRY:-}" ]]; then
        break
    fi
    log_info "CLI exited — restarting in ${RETRY_DELAY}s (Ctrl-C to stop)..."
    sleep "${RETRY_DELAY}"
done
