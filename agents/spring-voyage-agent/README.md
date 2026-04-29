# Spring Voyage Agent SDK — Python

Python SDK for building Spring Voyage agents. Implements the Bucket 1 contract
from [`docs/specs/agent-runtime-boundary.md`](../../docs/specs/agent-runtime-boundary.md).

## Install

```bash
pip install spring-voyage-agent
```

Or install from source in development:

```bash
pip install -e "agents/spring-voyage-agent[dev]"
```

## The three hooks

Implement these three async callables and hand them to `run()`:

| Hook | Signature | Called when |
|---|---|---|
| `initialize` | `async (context: IAgentContext) -> None` | Once at container start, before any message |
| `on_message` | `async (message: Message) -> AsyncIterator[Response]` | Once per inbound A2A message |
| `on_shutdown` | `async (reason: ShutdownReason) -> None` | Once on container SIGTERM |

## Context shape (`IAgentContext`)

Delivered to `initialize` from env vars and `/spring/context/` files.

| Field | Env var | Required |
|---|---|---|
| `tenant_id` | `SPRING_TENANT_ID` | yes |
| `agent_id` | `SPRING_AGENT_ID` | yes |
| `unit_id` | `SPRING_UNIT_ID` | no |
| `bucket2_url` | `SPRING_BUCKET2_URL` | yes |
| `bucket2_token` | `SPRING_BUCKET2_TOKEN` | yes |
| `llm_provider_url` | `SPRING_LLM_PROVIDER_URL` | yes |
| `llm_provider_token` | `SPRING_LLM_PROVIDER_TOKEN` | yes |
| `mcp_url` | `SPRING_MCP_URL` | yes |
| `mcp_token` | `SPRING_MCP_TOKEN` | yes |
| `telemetry_url` | `SPRING_TELEMETRY_URL` | yes |
| `telemetry_token` | `SPRING_TELEMETRY_TOKEN` | no |
| `workspace_path` | `SPRING_WORKSPACE_PATH` | yes |
| `concurrent_threads` | `SPRING_CONCURRENT_THREADS` | yes |

Structured files (agent definition, tenant config) are loaded from
`/spring/context/agent-definition.yaml` (or `.json`) and
`/spring/context/tenant-config.json`.

## Hello-world agent (~30 lines)

```python
# my_agent.py
from pathlib import Path
from spring_voyage_agent import IAgentContext, Message, Response, ShutdownReason, run


async def initialize(context: IAgentContext) -> None:
    """Wire up handles, open telemetry, inspect workspace."""
    workspace = Path(context.workspace_path)
    if (workspace / "state.txt").exists():
        print("Resuming from previous run")
    else:
        print(f"Starting fresh — agent {context.agent_id}")


async def on_message(message: Message):
    """Echo the inbound message back to the caller."""
    text = message.text or "(no text)"
    yield Response(text=f"Echo from {message.thread_id}: {text}", final=True)


async def on_shutdown(reason: ShutdownReason) -> None:
    """Flush state to workspace before exit."""
    print(f"Shutting down ({reason.value})")


if __name__ == "__main__":
    run(initialize=initialize, on_message=on_message, on_shutdown=on_shutdown)
```

Run it:

```bash
# Via the CLI entry point:
spring-voyage-agent --module my_agent

# Or directly:
python my_agent.py
```

## CLI

```
spring-voyage-agent --module <module> [--port <port>]
```

`<module>` is a Python import path (`my_agent`, `mypackage.agent`, etc.) that
exports the three hooks. The CLI imports the module, validates the hook names
are present, and starts the A2A server.

## SDK guarantees

- `initialize` completes before any `on_message` invocation.
- `on_message` is safe for concurrent re-entrant invocations across distinct
  threads when `concurrent_threads=true` (the default).
- When `concurrent_threads=false`, invocations are serialised globally.
- SIGTERM is trapped; `on_shutdown` is called and must complete within 30 s.
- If `initialize` fails or times out, the container exits non-zero.
