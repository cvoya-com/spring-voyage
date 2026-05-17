#!/usr/bin/env bash
# pool: fast
# `spring connector github filter set` preserves the existing GitHub
# binding config while replacing the per-binding inbound webhook filters.
# Issue #2407.
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
put_body="${tmpdir}/put-body.json"
stub="${tmpdir}/stub_api.py"

cat > "${stub}" <<'PY'
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import pathlib
import sys

PORT_FILE = pathlib.Path(sys.argv[1])
PUT_BODY = pathlib.Path(sys.argv[2])


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

    def do_GET(self):
        if self.path == "/api/v1/tenant/connectors/github/units/eng-team/config":
            self.write_json(200, {
                "unitId": "eng-team",
                "owner": "acme",
                "repo": "platform",
                "appInstallationId": 12345,
                "events": ["issues", "pull_request"],
                "reviewer": "alice",
                "eventsAreDefault": False,
                "add_on_assign": ["triage"],
                "remove_on_assign": ["needs-assignment"],
                "include_labels": [],
                "exclude_labels": [],
                "include_authors": [],
                "include_paths": [],
            })
            return

        self.write_json(404, {"title": "Not Found", "status": 404, "detail": self.path})

    def do_PUT(self):
        length = int(self.headers.get("Content-Length", "0") or "0")
        body = self.rfile.read(length).decode("utf-8") if length else ""
        PUT_BODY.write_text(body, encoding="utf-8")

        if self.path == "/api/v1/tenant/connectors/github/units/eng-team/config":
            payload = json.loads(body)
            payload["unitId"] = "eng-team"
            payload["eventsAreDefault"] = False
            self.write_json(200, payload)
            return

        self.write_json(404, {"title": "Not Found", "status": 404, "detail": self.path})


server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
PORT_FILE.write_text(str(server.server_address[1]), encoding="utf-8")
server.serve_forever()
PY

python3 "${stub}" "${port_file}" "${put_body}" >"${stub_log}" 2>&1 &
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

e2e::log "spring connector github filter set eng-team --include-label spring-voyage --exclude-label wip --include-author alice,bob --include-path docs/"
response="$(e2e::cli connector github filter set eng-team --include-label spring-voyage --exclude-label wip --include-author alice,bob --include-path docs/)"
code="${response##*$'\n'}"
body="${response%$'\n'*}"

e2e::expect_status "0" "${code}" "filter set succeeds"
e2e::expect_contains "Inbound filters updated for binding eng-team." "${body}" "filter set prints confirmation"

if python3 - "${put_body}" <<'PY'
import json
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
payload = json.loads(path.read_text(encoding="utf-8"))
# Existing fields preserved verbatim.
assert payload["owner"] == "acme"
assert payload["repo"] == "platform"
assert payload["appInstallationId"] == 12345
assert payload["events"] == ["issues", "pull_request"]
assert payload["reviewer"] == "alice"
assert payload["add_on_assign"] == ["triage"]
assert payload["remove_on_assign"] == ["needs-assignment"]
# New filter lists replaced.
assert payload["include_labels"] == ["spring-voyage"]
assert payload["exclude_labels"] == ["wip"]
# Comma-separated --include-author splits client-side.
assert payload["include_authors"] == ["alice", "bob"]
assert payload["include_paths"] == ["docs/"]
PY
then
    e2e::ok "filter set PUT preserves existing config and replaces filter lists"
else
    e2e::fail "filter set PUT body was not the expected merged config: $(<"${put_body}")"
fi

e2e::summary
