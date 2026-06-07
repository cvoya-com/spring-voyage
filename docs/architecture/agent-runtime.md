# Agent runtime

> **[Architecture index](README.md)** · Related: [Components](components.md), [Runtime flows](runtime-flows.md), [Messaging](messaging.md), [Deployment](deployment.md)

Spring Voyage does not implement an agentic loop. It **coordinates external
agent runtimes** — `claude`, `codex`, `gemini`, the `spring-voyage` agent —
running in containers ([ADR-0021](../decisions/0021-spring-voyage-is-not-an-agent-runtime.md)).
This page covers how a runtime is configured, launched, and driven for a turn:
the runtime catalogue, the launcher contract, the A2A sidecar bridge, the image
conformance contract, the AgentSDK, and credential handling.

---

## AgentRuntime, ModelProvider, Model

Three distinct identities ([ADR-0038](../decisions/0038-agent-runtime-and-model-provider-split.md)):

- **AgentRuntime** — *how* an agent runs: a container image, a launcher, and how
  the runtime binds a thread and receives its system prompt. Examples:
  `claude-code`, `codex`, `gemini`, `spring-voyage`.
- **ModelProvider** — *who serves the model*: one wire-format family
  (`anthropic`, `openai`-compatible, `google`) plus credential and live-model
  concerns. Shared across runtimes.
- **Model** — a `{provider, id}` pair. The provider is intrinsic to the model.

User-facing execution config collapses to `(runtime, model)`. The user picks a
runtime and a model; the provider follows from the model.

### The runtime catalogue

`runtime-catalog.yaml` is the checked-in source of truth, loaded by
`Cvoya.Spring.RuntimeCatalog`. It declares every `ModelProvider` and every
`AgentRuntime`, plus the **edges** between them — which `(runtime, provider,
authMethod)` triples are valid and which credential env-var each carries. Adding
a runtime or provider is a data change in this file plus, if a new wire format
or launch mechanism is involved, a small strategy registration. There are no
per-provider or per-runtime classes — `IAgentRuntimeLauncher` and
`IModelProviderAdapter` are small strategy registries keyed by id.

## The launch path

A turn runs through `A2AExecutionDispatcher` (worker-side):

1. **Resolve** the launcher for the agent's runtime from the catalogue.
2. **Build** the container config — image, env vars, the workspace, the system
   prompt, the per-turn MCP token.
3. **Start** the container (via the dispatcher — see [Deployment](deployment.md)).
4. **Probe** `/.well-known/agent.json` until the A2A endpoint is ready (≤60s).
5. **Send** the turn as an A2A `message/send`.
6. **Capture** the response and map it back to a platform `Message`.
7. **Tear down** (ephemeral) or leave running (persistent).

Both ephemeral and persistent hosting use this one path
([ADR-0025](../decisions/0025-unified-agent-launch-contract.md)); persistence is
a retention policy, not a separate dispatch surface.

### Launchers

An `IAgentRuntimeLauncher` turns an agent's resolved config into a launch spec —
image, argv, env vars, the workspace files, and the system prompt. The built-in
launchers are `ClaudeCodeLauncher`, `CodexLauncher`, `GeminiLauncher`, and
`SpringVoyageAgentLauncher`. Each describes the **workspace as pure data** — a
file map plus a mount path — and the dispatcher materialises it (see
[Deployment](deployment.md)). A launcher also writes the MCP server config the
runtime CLI reads.

### System-prompt delivery and `system_prompt_mode`

Every agent definition carries a `system_prompt_mode` field
([#2691](https://github.com/cvoya-com/spring-voyage/issues/2691),
[#2695](https://github.com/cvoya-com/spring-voyage/issues/2695)) on
its `execution` block. The cascade is *agent → unit → `append`*. The two values:

- `append` (default) — the platform's assembled prompt is **appended** to the
  runtime CLI's own default coding-assistant system prompt. Preserves the CLI's
  tool guidance and safety baseline. Right for engineer-shaped agents.
- `replace` — the platform's assembled prompt **replaces** the runtime CLI's
  default. Drops the coding-assistant persona entirely. Right for non-coding
  agents (routers, PMs, analysts).

The platform-managed system-prompt file lives at
`$SPRING_WORKSPACE_PATH/.spring/system-prompt.md` per
[ADR-0058](../decisions/0058-spring-voyage-container-contract.md) §2.2.2 (the
`.spring/` namespace for platform-owned workspace files). Writing it under
`.spring/` rather than the CLI's auto-discovered name keeps the platform
contract from colliding with any project clone the agent makes under its
workspace (e.g. an engineer agent cloning a repo whose root carries its own
`CLAUDE.md`).

Each runtime's per-mode flag-mapping is asymmetric — only Claude Code exposes
both modes natively:

| Runtime | `append` delivery | `replace` delivery | Asymmetry note |
|---|---|---|---|
| Claude Code (`claude`) | `--append-system-prompt-file <workspace>/.spring/system-prompt.md` | `--system-prompt-file <workspace>/.spring/system-prompt.md` | Both flags are mutually exclusive per the Claude CLI reference; the launcher emits exactly one. |
| Gemini (`gemini`) | Auto-discovered `GEMINI.md` at workspace root | `GEMINI_SYSTEM_MD=<workspace>/.spring/system-prompt.md` (env var) | Gemini's `GEMINI_SYSTEM_MD` is **replace-only** — gemini-cli 0.41.x has no append flag. Append mode therefore uses auto-discovery (the best Gemini offers) and Replace mode uses the env-var override. |
| Codex (`codex`) | Auto-discovered `AGENTS.md` at workspace root | (same — Codex has no flag) | Codex CLI has no `--system-prompt-*` flags ([openai/codex#11588](https://github.com/openai/codex/issues/11588)). `system_prompt_mode` on a Codex agent is honoured-by-best-effort only — Replace mode emits an informational log line and proceeds with `AGENTS.md`. Revisit when the upstream flags land. |
| Spring Voyage agent | (in-image; the Python agent reads `SPRING_SYSTEM_PROMPT` directly) | (same) | The native A2A runtime is not a CLI — `system_prompt_mode` is a CLI-shape concept. |

The launcher reads `AgentLaunchContext.SystemPromptMode` (cascade resolved at
dispatch time by `A2AExecutionDispatcher`) and selects the flag / env-var /
file path accordingly. The Codex limitation is the only case where the field's
declared semantics are not faithfully reflected in the runtime spawn.

## The A2A sidecar bridge

A runtime CLI like `claude` speaks stdin/stdout, not A2A. The platform bridges
that with the **A2A sidecar** (`Cvoya.Spring.AgentSidecar`, TypeScript), bundled
into every CLI-based agent image. The same binary runs in two modes:

**A2A bridge mode** (long-running, the image's ENTRYPOINT):

- exposes an **A2A 0.3.x** endpoint (`message/send`, `tasks/get`,
  `tasks/cancel`) on the agent port (default `8999`);
- on `message/send`, spawns the runtime CLI, pipes the prompt to stdin, captures
  stdout/stderr, and maps the result to an A2A task;
- reads the **per-turn MCP token** from the A2A `message/send` metadata and
  writes it to a workspace-resident token file
  (`$SPRING_WORKSPACE_PATH/.spring/bridge/mcp-token`) before spawning
  the CLI;
- propagates cancellation (`SIGTERM` → `SIGKILL`).

**MCP-server mode** (per-turn child, spawned by the CLI's MCP config —
`.mcp.json` for Claude Code / Gemini, `$CODEX_HOME/config.toml`
`[mcp_servers.spring-voyage]` for Codex (Codex does not read `.mcp.json`),
[ADR-0057](../decisions/0057-sidecar-local-mcp-server.md)):

- speaks MCP JSON-RPC 2.0 over stdio (no HTTP);
- reads the per-turn token from the workspace-resident token file at
  startup and holds it in memory for the process lifetime;
- proxies `initialize`, `tools/list`, `tools/call`, and other MCP
  requests onto the worker's `POST /mcp/` route, injecting
  `Authorization: Bearer <per-turn token>` on every call;
- caches `tools/list` for the lifetime of the process (= one turn).

## Image conformance

An agent image conforms by speaking A2A 0.3.x on the agent port. There are three
conformance paths ([ADR-0027](../decisions/0027-agent-image-conformance-contract.md)):

1. **`FROM` the OCI base image** — `ghcr.io/cvoya-com/spring-voyage-claude-code-base`
   bundles the sidecar bridge.
2. **The sidecar as an npm package** — for images that build their own base.
3. **A native A2A server** — an image that implements A2A 0.3.x directly (the
   `spring-voyage-agent` image takes this path).

The conformance probe is one step of the validation workflow (see
[Units & agents](units-and-agents.md)).

## The platform MCP surface in the container

A launched runtime reaches the platform's `sv.*` tools over MCP. Per
[ADR-0057](../decisions/0057-sidecar-local-mcp-server.md) the MCP server the
CLI dials is **sidecar-local** (stdio); the cross-network hop happens between
the sidecar and the worker, not between the CLI and the worker. The launcher
writes a one-server stdio config naming the sidecar binary as the MCP-server
command:

```jsonc
{
  "mcpServers": {
    "spring-voyage": {
      "command": "node",
      "args": ["/opt/spring-voyage/sidecar/dist/cli.js", "mcp"],
      "env": {
        "SPRING_MCP_PROXY_URL": "http://host.docker.internal:5050/mcp/",
        "SPRING_WORKSPACE_PATH": "/workspace"
      }
    }
  }
}
```

There is **no** `Authorization` header in this config — the CLI never sees
the per-turn token. Per turn, the long-running sidecar writes the token to
the workspace-resident token file; the per-turn MCP-server-mode child (spawned
by the CLI on each tool-use round) reads it from there and injects it on
outbound proxy calls to the worker.

`tools/list` is grant-filtered server-side (on the worker), so the runtime
discovers exactly the `sv.*` tools its subject is entitled to. The tool
catalogue and the single MCP server are described in [Messaging](messaging.md).

## The AgentSDK

`Cvoya.Spring.AgentSdk` is a typed client for **workflow-driven** runtime images
— images that run a deterministic workflow rather than an LLM tool-use loop and
want to call the messaging tools as method calls. Its `MessagingClient` calls
`sv.messaging.send` / `sv.messaging.multicast` over JSON-RPC `tools/call`
against the same MCP server, with the same per-turn session token. The image
author chooses the LLM-driven or workflow-driven shape; the platform does not
branch — both reach the same delivery handlers.

Workflow-driven runtimes follow the **workflow-as-container** model
([ADR-0019](../decisions/0019-workflow-as-container.md)): the workflow ships as
its own image with its own Dapr sidecar, decoupled from the platform's release
cycle. Workflow-state durability is the image author's concern, not the
platform's.

## Credentials

An agent container carries two credential surfaces, kept distinct:

- **The per-turn MCP session token** — issued by the worker `McpServer`,
  delivered in the A2A `message/send`, written to the workspace-resident
  MCP token file by the sidecar, revoked at turn-end. The per-turn
  MCP-server-mode child reads it from the token file and authenticates every
  `sv.*` tool call on the worker. The CLI itself never sees the token; the
  trust boundary is platform↔sidecar only. See
  [ADR-0054](../decisions/0054-one-mcp-server-one-execution-host.md) and
  [ADR-0057](../decisions/0057-sidecar-local-mcp-server.md).
- **The model-provider credential** — the API key or token the runtime CLI uses
  to call its LLM. Credentials are keyed `(tenant, provider, authMethod)` with
  unit→tenant inheritance ([ADR-0003](../decisions/0003-secret-inheritance-unit-to-tenant.md));
  the catalogue edge names the env-var the runtime expects. Storage and the
  secrets stack are covered in [Security](security.md).

**Rotation is restart-driven.** A persistent agent's credentials are
re-injected by rebuilding its launch context and restarting the container —
there is no in-place credential refresh. The supervisor rebuilds the context via
`IAgentContextBuilder` on restart, so a rotated secret is picked up on the next
container start.
