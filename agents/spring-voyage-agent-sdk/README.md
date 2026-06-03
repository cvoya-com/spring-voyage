# Spring Voyage Agent SDK — Python

Python SDK for building Spring Voyage agents. Implements the Bucket 1 contract
from [`docs/specs/agent-runtime-boundary.md`](../../docs/specs/agent-runtime-boundary.md).

## Install

```bash
pip install spring-voyage-agent-sdk
```

Or install from source in development:

```bash
pip install -e "agents/spring-voyage-agent-sdk[dev]"
```

## The three hooks

Implement these three async callables and hand them to `run()`:

| Hook | Signature | Called when |
|---|---|---|
| `initialize` | `async (context: IAgentContext) -> None` | Once at container start, before any message |
| `on_message` | `async (message: Message) -> AsyncIterator[Response]` | Once per inbound A2A message |
| `on_shutdown` | `async (reason: ShutdownReason) -> None` | Once on container SIGTERM |

## Context shape (`IAgentContext`)

Delivered to `initialize` from env vars and one workspace file
(`.spring/system-prompt.md`).

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

The platform-assembled system prompt (instructions, identity, equipped
skills, connector context) is loaded from
`$SPRING_WORKSPACE_PATH/.spring/system-prompt.md` and exposed as
`context.system_prompt`. Use it as the system message for any LLM call
the agent issues.

## Hello-world agent (~30 lines)

```python
# my_agent.py
from pathlib import Path
from spring_voyage_agent_sdk import IAgentContext, Message, Response, ShutdownReason, run


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
spring-voyage-agent-sdk --module my_agent

# Or directly:
python my_agent.py
```

## CLI

```
spring-voyage-agent-sdk --module <module> [--port <port>]
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

## Per-thread state

Every inbound message exposes `message.thread_id` (the A2A `Message.context_id`).
On-disk per-conversation scratch lives under `$SPRING_WORKSPACE_PATH/work/<id>/`
(the `<id>` segment is opaque and platform-managed) — use
`IAgentContext.thread_workspace(thread_id)` to resolve the directory (it is
created on first access):

```python
async def on_message(message: Message):
    workspace = context.thread_workspace(message.thread_id)
    (workspace / "state.json").write_text(...)
```

In-memory per-thread state is safe under `concurrent_threads=false` (the
default — mailbox serialises invocations). Under `concurrent_threads=true`,
follow the author contract in
[ADR-0041](../../docs/decisions/0041-actor-runtime-contract.md): no fixed
ports, no shared globals, and keep thread-local files inside the per-thread
workspace directory.

See also: [`docs/architecture/agent-sdk.md` § "Per-Thread State"](../../docs/architecture/agent-sdk.md).
