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
#   4. `eng/config/spring.env` exists and contains `GitHub__WebhookSecret`
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
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
CONFIG_DIR="${REPO_ROOT}/eng/config"

REPO=""
URL="${GH_WEBHOOK_URL:-http://localhost/api/v1/webhooks/github}"
ENV_FILE="${GH_WEBHOOK_ENV_FILE:-${CONFIG_DIR}/spring.env}"
EVENTS="${GH_WEBHOOK_EVENTS:-}"

# ANSI colors — emitted only when stderr is a TTY and NO_COLOR is unset
# (https://no-color.org/).
if [[ -t 2 && -z "${NO_COLOR:-}" ]]; then
    C_RED=$'\033[31m'
    C_GREEN=$'\033[32m'
    C_YELLOW=$'\033[33m'
    C_CYAN=$'\033[36m'
    C_DIM=$'\033[2m'
    C_RESET=$'\033[0m'
else
    C_RED='' C_GREEN='' C_YELLOW='' C_CYAN='' C_DIM='' C_RESET=''
fi

log()      { printf '[gh-webhook-forward] %s\n' "$*" >&2; }
log_warn() { printf '%s[gh-webhook-forward] %s%s\n' "${C_YELLOW}" "$*" "${C_RESET}" >&2; }
log_err()  { printf '%s[gh-webhook-forward] %s%s\n' "${C_RED}"    "$*" "${C_RESET}" >&2; }
log_info() { printf '%s[gh-webhook-forward] %s%s\n' "${C_CYAN}"   "$*" "${C_RESET}" >&2; }
die()      { log_err "error: $*"; exit 1; }

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
                          Default: <repo>/eng/config/spring.env
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

# Delete any stale forwarding webhook left by a previous run.
# gh webhook forward registers a hook at webhook-forwarder.github.com (name="cli").
# If the script is killed before gh can clean up, that hook stays on GitHub and
# causes 422 "Hook already exists" on the next run.
cleanup_stale_forward_hooks() {
    local hooks
    hooks="$(gh api "repos/${REPO}/hooks" 2>/dev/null)" || {
        log_warn "warning: could not list webhooks — skipping stale-hook cleanup"
        return 0
    }
    while IFS=$'\t' read -r hook_id hook_url; do
        [[ -n "${hook_id}" ]] || continue
        log "removing stale forwarding webhook id=${hook_id} (${hook_url})..."
        if gh api -X DELETE "repos/${REPO}/hooks/${hook_id}" >/dev/null 2>&1; then
            log "  removed."
        else
            log_warn "  warning: could not remove webhook id=${hook_id} — may already be gone."
        fi
    done < <(printf '%s' "${hooks}" | python3 -c "
import json, sys
for h in json.load(sys.stdin):
    url = h.get('config', {}).get('url', '')
    name = h.get('name', '')
    if 'webhook-forwarder.github.com' in url or name == 'cli':
        print(str(h['id']) + '\t' + url)
")
}

# Filter for `gh webhook forward`'s combined stdout+stderr.
#
# Translates the noisy bits into one-liners:
#   - "Forwarding Webhook events from GitHub..." -> "connected" / "reconnected" (green)
#   - "Error: ..."                               -> "disconnect: ..." (yellow)
#   - "warning: error forwarding event: ..."     -> red passthrough
#   - "[LOG] received event ..."                 -> green passthrough
# Suppresses the cobra help block (everything from "Usage:" to end-of-stream),
# which gh emits whenever the forward subcommand exits with an error. Each gh
# subprocess emits at most one such block, so a stream-wide suppression flag
# is sufficient.
# Other lines from gh are passed through with a dim "[gh]" prefix so they
# remain visible but are visually distinct from this script's own output.
filter_gh_output() {
    local attempt="${1:-0}"
    awk \
        -v ATTEMPT="${attempt}" \
        -v C_GREEN="${C_GREEN}" \
        -v C_YELLOW="${C_YELLOW}" \
        -v C_RED="${C_RED}" \
        -v C_DIM="${C_DIM}" \
        -v C_RESET="${C_RESET}" '
    BEGIN { in_help = 0 }

    /^Usage:/ { in_help = 1 }
    in_help   { next }

    /^Forwarding Webhook events from GitHub/ {
        verb = (ATTEMPT == "0") ? "connected" : "reconnected"
        printf "%s[gh-webhook-forward] %s — waiting for events%s\n", C_GREEN, verb, C_RESET
        fflush()
        next
    }

    /^Error:/ {
        msg = $0
        sub(/^Error:[ \t]*/, "", msg)
        printf "%s[gh-webhook-forward] disconnect: %s%s\n", C_YELLOW, msg, C_RESET
        fflush()
        next
    }

    /^warning: error forwarding event:/ {
        printf "%s[gh-webhook-forward] %s%s\n", C_RED, $0, C_RESET
        fflush()
        next
    }

    /^\[LOG\] received event/ {
        printf "%s[gh-webhook-forward] %s%s\n", C_GREEN, $0, C_RESET
        fflush()
        next
    }

    /^[[:space:]]*$/ { next }

    {
        printf "%s[gh] %s%s\n", C_DIM, $0, C_RESET
        fflush()
    }
    '
}

attempt=0
while true; do
    cleanup_stale_forward_hooks
    # Merge gh's stderr into stdout, run through the filter, and redirect the
    # filter back to stderr so script-wide logging stays on a single stream.
    # `|| true` keeps the retry loop running across gh exit codes (and across
    # `set -o pipefail` triggered by gh exiting non-zero).
    { gh "${forward_args[@]}" 2>&1 | filter_gh_output "${attempt}" >&2; } || true
    log_info "reconnecting in ${RETRY_DELAY}s (Ctrl-C to stop)..."
    sleep "${RETRY_DELAY}"
    attempt=$((attempt + 1))
done
