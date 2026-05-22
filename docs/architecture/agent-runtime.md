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

## The A2A sidecar bridge

A runtime CLI like `claude` speaks stdin/stdout, not A2A. The platform bridges
that with the **A2A sidecar** (`Cvoya.Spring.AgentSidecar`, TypeScript), bundled
into every CLI-based agent image. It:

- exposes an **A2A 0.3.x** endpoint (`message/send`, `tasks/get`,
  `tasks/cancel`) on the agent port (default `8999`);
- on `message/send`, spawns the runtime CLI, pipes the prompt to stdin, captures
  stdout/stderr, and maps the result to an A2A task;
- reads the **per-turn MCP token** from the A2A `message/send` metadata and
  writes it into the runtime's MCP config (`.mcp.json` for `claude`/`codex`,
  `.gemini/settings.json` for `gemini`) before spawning the CLI;
- propagates cancellation (`SIGTERM` → `SIGKILL`).

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

A launched runtime reaches the platform's `sv.*` tools over MCP. The launcher
writes a one-server config naming the worker `McpServer`:

```jsonc
{
  "mcpServers": {
    "spring-voyage": {
      "type": "http",
      "url": "http://host.docker.internal:5050/mcp/",
      "headers": { "Authorization": "Bearer <per-turn MCP session token>" }
    }
  }
}
```

`tools/list` is grant-filtered server-side, so the runtime discovers exactly the
`sv.*` tools its subject is entitled to. The tool catalogue and the single MCP
server are described in [Messaging](messaging.md).

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
  delivered in the A2A `message/send`, written into the MCP config by the
  sidecar, revoked at turn-end. It authenticates every `sv.*` tool call. See
  [ADR-0054](../decisions/0054-one-mcp-server-one-execution-host.md).
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
