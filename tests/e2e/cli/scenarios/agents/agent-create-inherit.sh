#!/usr/bin/env bash
# pool: fast
# `spring agent create --inherit` sends no execution block on the public API
# request body, allowing the platform to resolve execution from parent units.
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

cat > "${stub}" <<'PY'
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import pathlib
import sys

PORT_FILE = pathlib.Path(sys.argv[1])
REQUEST_LOG = pathlib.Path(sys.argv[2])

UNIT_D = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"


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
                "name": "ada-test",
                "displayName": "ada-test",
                "role": None,
                "unitIds": [UNIT_D],
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

export SPRING_API_URL="http://127.0.0.1:$(<"${port_file}")"
unit_id="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"

e2e::log "spring agent create --name ada-test --unit ${unit_id} --inherit"
response="$(e2e::cli_agent_create --name ada-test --unit "${unit_id}" --inherit)"
code="${response##*$'\n'}"

e2e::expect_status "0" "${code}" "agent create with inherit succeeds"

if validation_error="$(python3 - "${request_log}" <<'PY' 2>&1
import json
import pathlib
import sys

request_log = pathlib.Path(sys.argv[1])
lines = request_log.read_text(encoding="utf-8").splitlines()
posts = []
for idx, line in enumerate(lines):
    if line.startswith("POST ") and idx + 1 < len(lines):
        posts.append((line.removeprefix("POST "), lines[idx + 1]))

if len(posts) != 1:
    raise SystemExit(f"expected exactly one POST request, got {len(posts)}: {request_log.read_text(encoding='utf-8')}")

path, body = posts[0]
if path != "/api/v1/tenant/agents":
    raise SystemExit(f"expected POST /api/v1/tenant/agents, got POST {path}")

payload = json.loads(body)
definition_json = payload.get("definitionJson")
if definition_json in (None, ""):
    raise SystemExit(0)

definition = json.loads(definition_json)
if isinstance(definition, dict) and "execution" in definition:
    raise SystemExit(f"definitionJson contains execution: {definition_json}")
PY
)"; then
    e2e::ok "stub API received the agent create request without execution config"
else
    e2e::fail "agent create inherit request validation failed: ${validation_error}"
fi

e2e::summary
