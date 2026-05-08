#!/usr/bin/env bash
# pool: fast
# `spring agent create` surfaces the structured ADR-0039 multi-parent
# inheritance conflict response as one line per conflicting field.
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

UNIT_A_N = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
UNIT_B_N = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
UNIT_A_D = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
UNIT_B_D = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"


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
            self.write_json(422, {
                "error": "MultiParentInheritanceConflict",
                "conflictingFields": {
                    "runtime": [
                        {"source": UNIT_A_N, "value": "claude-code"},
                        {"source": UNIT_B_N, "value": "spring-voyage"},
                    ],
                },
            })
            return

        self.write_json(404, {"title": "Not Found", "status": 404})

    def do_GET(self):
        with REQUEST_LOG.open("a", encoding="utf-8") as log:
            log.write(f"GET {self.path}\n")

        if self.path == f"/api/v1/tenant/units/{UNIT_A_N}":
            self.write_json(200, {
                "unit": {
                    "id": UNIT_A_D,
                    "name": "unit-engineering",
                    "displayName": "unit-engineering",
                },
                "details": None,
            })
            return

        if self.path == f"/api/v1/tenant/units/{UNIT_B_N}":
            self.write_json(200, {
                "unit": {
                    "id": UNIT_B_D,
                    "name": "unit-support",
                    "displayName": "unit-support",
                },
                "details": None,
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
unit_a="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
unit_b="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

e2e::log "spring agent create --name ada-conflict --unit ${unit_a} --unit ${unit_b}"
response="$(e2e::cli_agent_create --name ada-conflict --unit "${unit_a}" --unit "${unit_b}")"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

e2e::expect_status "1" "${code}" "agent create exits with validation failure"
expected="runtime: unit-engineering=claude-code, unit-support=spring-voyage"
if grep -Fxq "${expected}" <<<"${body}"; then
    e2e::ok "agent create prints the structured conflict line"
else
    e2e::fail "agent create output did not match '${expected}': ${body:0:500}"
fi

if grep -Fq "POST /api/v1/tenant/agents" "${request_log}"; then
    e2e::ok "stub API received the agent create request"
else
    e2e::fail "stub API did not receive the agent create request"
fi

e2e::summary
