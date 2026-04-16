# A2A Sidecar Adapter

A lightweight Python process that wraps any CLI-based agent behind an [A2A (Agent-to-Agent)](https://a2a-protocol.org/) endpoint. Spring Voyage launches this sidecar inside the agent container; the platform dispatcher (`A2AExecutionDispatcher`) communicates with agents exclusively through A2A.

## How it works

1. The sidecar starts an HTTP server on `AGENT_PORT` (default `8999`).
2. It serves an Agent Card at `/.well-known/agent.json`.
3. On `message/send` (JSON-RPC): it spawns the CLI agent process (`AGENT_CMD`), pipes the user message to stdin, collects stdout/stderr, and returns an A2A task response.
4. On `tasks/cancel`: it sends `SIGTERM` to the running CLI process.

## Environment variables

| Variable | Default | Description |
|---|---|---|
| `AGENT_CMD` | `claude` | CLI command to launch |
| `AGENT_ARGS` | (empty) | Space-separated CLI arguments |
| `AGENT_NAME` | `CLI Agent` | Display name in the Agent Card |
| `AGENT_PORT` | `8999` | Port the sidecar listens on |
| `SPRING_SYSTEM_PROMPT` | — | System prompt (passed as env to CLI) |
| `SPRING_MCP_ENDPOINT` | — | MCP server URL |
| `SPRING_AGENT_TOKEN` | — | Bearer token for MCP |

## Build

```bash
# Standalone
pip install -r requirements.txt
python3 sidecar.py

# Docker
docker build -t spring-a2a-sidecar .
docker run -p 8999:8999 -e AGENT_CMD=echo spring-a2a-sidecar
```

## Reusability

The sidecar is agent-agnostic. Change `AGENT_CMD` to wrap different CLI agents:

- **Claude Code**: `AGENT_CMD=claude`
- **Codex**: `AGENT_CMD=codex`
- **Gemini**: `AGENT_CMD=gemini`
- **Custom**: any binary that reads stdin and writes to stdout
