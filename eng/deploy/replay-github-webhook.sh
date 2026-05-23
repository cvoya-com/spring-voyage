#!/usr/bin/env bash
# Spring Voyage — replay a GitHub webhook delivery against a local API.
#
# Signs a raw GitHub webhook payload with the deployment's
# `GitHub__WebhookSecret` and POSTs it to the Spring Voyage webhook
# endpoint — exactly as `gh webhook forward` (gh-webhook-forward.sh)
# would, but from a saved payload file. This makes connector behaviour
# reproducible in a fixes -> build -> deploy -> test loop without
# depending on GitHub or the long-poll forwarder.
#
# The payload file must be the *raw* GitHub event body (the JSON GitHub
# POSTs to a webhook), not the Spring Voyage internal message. For an
# `issues` / `labeled` event that means a top-level object with
# `action`, `label`, `issue`, `repository`, and `sender`.
#
# Usage:
#
#   ./replay-github-webhook.sh --payload /tmp/gh-2535-labeled.json
#   ./replay-github-webhook.sh --payload p.json --event issues --delivery "$(uuidgen)"
#   ./replay-github-webhook.sh --payload p.json --url http://localhost:5000/api/v1/webhooks/github
#
# Preconditions:
#
#   1. `openssl` and `curl` on PATH.
#   2. `eng/config/spring.env` exists and contains `GitHub__WebhookSecret`
#      (the same value the API verifies against). Written by setup.sh.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
CONFIG_DIR="${REPO_ROOT}/eng/config"

PAYLOAD=""
EVENT="issues"
URL="${GH_WEBHOOK_URL:-http://localhost/api/v1/webhooks/github}"
ENV_FILE="${GH_WEBHOOK_ENV_FILE:-${CONFIG_DIR}/spring.env}"
DELIVERY=""

log() { printf '[replay-github-webhook] %s\n' "$*" >&2; }
die() { printf '[replay-github-webhook][error] %s\n' "$*" >&2; exit 1; }

usage() {
    cat >&2 <<'USAGE'
Usage: replay-github-webhook.sh --payload FILE [--event TYPE] [--url URL] [--env PATH] [--delivery UUID]

Required:
  --payload FILE    Raw GitHub webhook payload (JSON) to replay.

Optional:
  --event TYPE      X-GitHub-Event header value. Default: issues.
  --url URL         Local webhook endpoint. Default: http://localhost/api/v1/webhooks/github
                    (or $GH_WEBHOOK_URL).
  --env PATH        Env file holding GitHub__WebhookSecret. Default: <repo>/eng/config/spring.env
                    (or $GH_WEBHOOK_ENV_FILE).
  --delivery UUID   X-GitHub-Delivery header value. Default: a fresh uuid.
  -h, --help        Show this help and exit.
USAGE
}

while (( $# > 0 )); do
    case "$1" in
        --payload)  [[ $# -ge 2 ]] || die "--payload requires a path";  PAYLOAD="$2";  shift 2 ;;
        --payload=*) PAYLOAD="${1#--payload=}"; shift ;;
        --event)    [[ $# -ge 2 ]] || die "--event requires a value";   EVENT="$2";    shift 2 ;;
        --event=*)  EVENT="${1#--event=}"; shift ;;
        --url)      [[ $# -ge 2 ]] || die "--url requires a value";      URL="$2";      shift 2 ;;
        --url=*)    URL="${1#--url=}"; shift ;;
        --env)      [[ $# -ge 2 ]] || die "--env requires a path";       ENV_FILE="$2"; shift 2 ;;
        --env=*)    ENV_FILE="${1#--env=}"; shift ;;
        --delivery) [[ $# -ge 2 ]] || die "--delivery requires a value"; DELIVERY="$2"; shift 2 ;;
        --delivery=*) DELIVERY="${1#--delivery=}"; shift ;;
        -h|--help)  usage; exit 0 ;;
        *)          usage; die "unknown argument: $1" ;;
    esac
done

[[ -n "${PAYLOAD}" ]] || { usage; die "--payload FILE is required"; }
[[ -f "${PAYLOAD}" ]] || die "payload file not found: ${PAYLOAD}"

command -v openssl >/dev/null 2>&1 || die "openssl not found on PATH."
command -v curl    >/dev/null 2>&1 || die "curl not found on PATH."

[[ -f "${ENV_FILE}" ]] \
    || die "env file not found: ${ENV_FILE}
       Run eng/deploy/setup.sh first, or pass --env <path>."

# Read GitHub__WebhookSecret without sourcing the file (it may contain
# shell-hostile values). Mirrors gh-webhook-forward.sh.
SECRET="$(awk -F= '
    /^[[:space:]]*#/ { next }
    /^[[:space:]]*$/ { next }
    $1 ~ /^[[:space:]]*GitHub__WebhookSecret[[:space:]]*$/ {
        sub(/^[^=]*=/, "", $0)
        gsub(/^[[:space:]]+|[[:space:]]+$/, "", $0)
        if ($0 ~ /^".*"$/ || $0 ~ /^'\''.*'\''$/) { $0 = substr($0, 2, length($0) - 2) }
        print $0
        exit
    }
' "${ENV_FILE}")"

[[ -n "${SECRET}" ]] \
    || die "GitHub__WebhookSecret is not set in ${ENV_FILE}."

if [[ -z "${DELIVERY}" ]]; then
    DELIVERY="$(uuidgen 2>/dev/null | tr 'A-Z' 'a-z' || openssl rand -hex 16)"
fi

# HMAC-SHA256 over the exact bytes of the payload file — the same bytes
# curl sends with --data-binary, so the API's signature check matches.
SIGNATURE="sha256=$(openssl dgst -sha256 -hmac "${SECRET}" -hex < "${PAYLOAD}" | awk '{print $NF}')"

log "POST ${URL}  event=${EVENT}  delivery=${DELIVERY}  bytes=$(wc -c < "${PAYLOAD}" | tr -d ' ')"

HTTP_CODE="$(curl -sS -o /dev/stderr -w '%{http_code}' \
    -X POST "${URL}" \
    -H "Content-Type: application/json" \
    -H "X-GitHub-Event: ${EVENT}" \
    -H "X-GitHub-Delivery: ${DELIVERY}" \
    -H "X-Hub-Signature-256: ${SIGNATURE}" \
    --data-binary "@${PAYLOAD}")"

echo >&2
log "HTTP ${HTTP_CODE}"
case "${HTTP_CODE}" in
    202) log "accepted — the connector translated and routed the event." ;;
    401) die "401 — signature rejected. Is GitHub__WebhookSecret current for this deployment?" ;;
    *)   die "unexpected status ${HTTP_CODE}." ;;
esac
