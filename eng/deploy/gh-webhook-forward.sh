#!/usr/bin/env bash
# Spring Voyage — local-dev webhook forwarder using `gh webhook forward`.
#
# Streams GitHub webhook deliveries for a single repository (the one
# passed via --repo) over the operator's authenticated `gh` session and
# replays each delivery against a locally running Spring Voyage API.
# This is the recommended local-dev recipe for receiving webhooks
# without provisioning a public tunnel.
#
# How it works:
#
#   github.com ----> gh CLI (operator's authenticated session)
#                       |
#                       | (gh webhook forward; long-poll over HTTPS)
#                       v
#                  http://localhost:8080/api/v1/webhooks/github
#                       |
#                       v
#                  spring-api on the dev machine
#
# Preconditions:
#
#   1. `gh` installed and `gh auth login` completed.
#   2. The `gh-webhook` extension installed:
#         gh extension install cli/gh-webhook
#   3. Repository admin permission on --repo (required by GitHub to
#      register the temporary forwarding hook).
#   4. `eng/deploy/spring.env` exists and contains `GitHub__WebhookSecret`
#      (so `gh` signs forwarded payloads with the same secret the API
#      verifies against). The deployment's setup.sh writes this value.
#
# Usage:
#
#   ./gh-webhook-forward.sh --repo owner/repo
#   ./gh-webhook-forward.sh --repo owner/repo --url http://localhost:5000/api/v1/webhooks/github
#   ./gh-webhook-forward.sh --repo owner/repo --env /custom/path/spring.env
#
# Stop with Ctrl-C.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

REPO=""
URL="${GH_WEBHOOK_URL:-http://localhost/api/v1/webhooks/github}"
ENV_FILE="${GH_WEBHOOK_ENV_FILE:-${SCRIPT_DIR}/spring.env}"
EVENTS="${GH_WEBHOOK_EVENTS:-}"

log() { printf '[gh-webhook-forward] %s\n' "$*" >&2; }
die() { printf '[gh-webhook-forward][error] %s\n' "$*" >&2; exit 1; }

usage() {
    cat >&2 <<'USAGE'
Usage: gh-webhook-forward.sh --repo owner/repo [--url URL] [--env PATH] [--events list]

Required:
  --repo owner/repo       GitHub repository to forward deliveries from.

Optional:
  --url URL               Local webhook endpoint to deliver to.
                          Default: http://localhost:8080/api/v1/webhooks/github
                          (or $GH_WEBHOOK_URL).
  --env PATH              Path to the env file holding GitHub__WebhookSecret.
                          Default: <script-dir>/spring.env
                          (or $GH_WEBHOOK_ENV_FILE).
  --events list           Comma-separated events to forward (e.g. "issues,pull_request").
                          Default: "*" (all events the App is subscribed to)
                          (or $GH_WEBHOOK_EVENTS).
  -h, --help              Show this help and exit.
USAGE
}

while (( $# > 0 )); do
    case "$1" in
        --repo)
            [[ $# -ge 2 ]] || die "--repo requires a value (owner/repo)"
            REPO="$2"
            shift 2
            ;;
        --repo=*)
            REPO="${1#--repo=}"
            shift
            ;;
        --url)
            [[ $# -ge 2 ]] || die "--url requires a value"
            URL="$2"
            shift 2
            ;;
        --url=*)
            URL="${1#--url=}"
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
        --events)
            [[ $# -ge 2 ]] || die "--events requires a value"
            EVENTS="$2"
            shift 2
            ;;
        --events=*)
            EVENTS="${1#--events=}"
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

[[ -n "${REPO}" ]] || { usage; die "--repo owner/repo is required"; }
[[ "${REPO}" == */* ]] || die "--repo must be owner/repo (got '${REPO}')"

# --- Preflight ---------------------------------------------------------------

command -v gh >/dev/null 2>&1 \
    || die "gh CLI not found on PATH. Install it from https://cli.github.com/ and run 'gh auth login'."

if ! gh auth status >/dev/null 2>&1; then
    die "gh is not authenticated. Run 'gh auth login' (a personal token with repo-admin permission on ${REPO} is required)."
fi

if ! gh extension list 2>/dev/null | grep -qF 'cli/gh-webhook'; then
    die "The 'gh-webhook' extension is not installed.
       Install it manually with:
           gh extension install cli/gh-webhook
       Then re-run this script."
fi

[[ -f "${ENV_FILE}" ]] \
    || die "env file not found: ${ENV_FILE}
       Run eng/deploy/setup.sh first, or pass --env <path> to point at a different file."

# Read GitHub__WebhookSecret from the env file without sourcing it (the file
# may contain other shell-hostile values, single-quoted PEMs, etc.).
SECRET="$(awk -F= '
    /^[[:space:]]*#/        { next }
    /^[[:space:]]*$/        { next }
    $1 ~ /^[[:space:]]*GitHub__WebhookSecret[[:space:]]*$/ {
        sub(/^[^=]*=/, "", $0)
        gsub(/^[[:space:]]+|[[:space:]]+$/, "", $0)
        # Strip surrounding single or double quotes if present.
        if ($0 ~ /^".*"$/ || $0 ~ /^'\''.*'\''$/) {
            $0 = substr($0, 2, length($0) - 2)
        }
        print $0
        exit
    }
' "${ENV_FILE}")"

[[ -n "${SECRET}" ]] \
    || die "GitHub__WebhookSecret is not set in ${ENV_FILE}.
       Run eng/deploy/setup.sh (or 'spring github-app register') so the
       per-deployment webhook secret is provisioned."

# --- Forward -----------------------------------------------------------------

log "forwarding webhooks for ${REPO} -> ${URL}"
log "stop with Ctrl-C"

forward_args=( webhook forward --repo "${REPO}" --url "${URL}" --secret "${SECRET}" )
if [[ -n "${EVENTS}" ]]; then
    forward_args+=( --events "${EVENTS}" )
else
    forward_args+=( --events "*" )
fi

RETRY_DELAY=5

while true; do
    gh "${forward_args[@]}" || true
    log "connection dropped — reconnecting in ${RETRY_DELAY}s (Ctrl-C to stop)..."
    sleep "${RETRY_DELAY}"
done
