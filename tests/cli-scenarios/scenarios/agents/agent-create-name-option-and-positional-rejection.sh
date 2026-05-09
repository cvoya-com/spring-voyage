#!/usr/bin/env bash
# pool: fast
# `spring agent create` accepts --name and rejects the removed positional name.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${HERE}/../../_lib.sh"

if ! command -v python3 >/dev/null 2>&1; then
    e2e::fail "python3 is required for the local stub API"
    e2e::summary
    exit 1
fi

tmpdir="$(mktemp -d)"
port_file="${tmpdir}/port"
stub_log="${tmpdir}/stub.log"
request_log="${tmpdir}/requests.log"
stub="${tmpdir}/stub_api.py"
touch "${request_log}"

cat > "${stub}" <<'PY'
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import pathlib
import sys

PORT_FILE = pathlib.Path(sys.argv[1])
REQUEST_LOG = pathlib.Path(sys.argv[2])


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        return

    def write_json(self, status, payload):
        body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0") or "0")
        body = self.rfile.read(length).decode("utf-8") if length else ""
        with REQUEST_LOG.open("a", encoding="utf-8") as log:
            log.write(f"POST {self.path}\n{body}\n")

        if self.path == "/api/v1/tenant/agents":
            self.write_json(201, {
                "id": "11111111-1111-1111-1111-111111111111",
                "name": "my-agent",
                "displayName": "my-agent",
                "role": None,
                "unitIds": [],
            })
            return

        self.write_json(404, {"title": "Not Found", "status": 404})


server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
PORT_FILE.write_text(str(server.server_address[1]), encoding="utf-8")
server.serve_forever()
PY

python3 "${stub}" "${port_file}" "${request_log}" >"${stub_log}" 2>&1 &
stub_pid=$!

cleanup() {
    local rc=$?
    if kill -0 "${stub_pid}" >/dev/null 2>&1; then
        kill "${stub_pid}" >/dev/null 2>&1 || true
        wait "${stub_pid}" >/dev/null 2>&1 || true
    fi
    rm -rf "${tmpdir}"
    return "${rc}"
}
trap cleanup EXIT

for _ in {1..50}; do
    [[ -s "${port_file}" ]] && break
    sleep 0.1
done

if [[ ! -s "${port_file}" ]]; then
    e2e::fail "stub API did not start: $(<"${stub_log}")"
    e2e::summary
    exit 1
fi

SPRING_API_URL="http://127.0.0.1:$(<"${port_file}")"
export SPRING_API_URL

e2e::log "spring agent create --name my-agent"
response="$(e2e::cli_agent_create --name my-agent)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

if [[ "${code}" == "0" ]]; then
    e2e::ok "agent create with --name succeeds (status ${code})"
else
    e2e::fail "agent create with --name succeeds - expected 0, got ${code}: ${body:0:500}"
fi

legacy_message="Positional <name> was removed in ADR-0039. Use --name <display-name> to set the agent's display name."

e2e::log "spring agent create my-agent (removed positional)"
reject_response="$(e2e::cli agent create my-agent)"
reject_code="${reject_response##*$'\n'}"
reject_body="${reject_response%$'\n'*}"

if [[ "${reject_code}" != "0" ]]; then
    e2e::ok "agent create positional exits non-zero"
else
    e2e::fail "agent create positional unexpectedly succeeded"
fi

e2e::expect_contains "${legacy_message}" "${reject_body}" "positional rejection prints the ADR-0039 migration hint"

if validation_error="$(python3 - "${request_log}" <<'PY' 2>&1
import json
import pathlib
import sys

lines = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8").splitlines()
posts = [(lines[i].split(" ", 1)[1], lines[i + 1]) for i in range(len(lines) - 1) if lines[i].startswith("POST ")]
if len(posts) != 1:
    raise SystemExit(f"expected exactly one POST, got {len(posts)}")
path, body = posts[0]
if path != "/api/v1/tenant/agents":
    raise SystemExit(f"unexpected path: {path}")
payload = json.loads(body)
if payload.get("displayName") != "my-agent":
    raise SystemExit(f"unexpected displayName: {payload.get('displayName')}")
if payload.get("unitIds") != []:
    raise SystemExit(f"expected unitIds=[], got {payload.get('unitIds')}")
PY
)"; then
    e2e::ok "--name happy path posts the expected agent create request"
else
    e2e::fail "agent create --name request mismatch: ${validation_error}"
fi

e2e::summary
