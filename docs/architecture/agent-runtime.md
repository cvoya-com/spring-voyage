# Agent Runtime

> **[Architecture Index](README.md)** | Related: [Workflows](workflows.md), [Units & Agents](units.md), [Deployment](deployment.md), [Messaging](messaging.md)

---

This document describes how the platform turns a single inbound message to an
`agent://` address into an actual agent turn. The layers, in order of
appearance on the dispatch path, are:

1. **`A2AExecutionDispatcher`** — the single entry point invoked by the
   `AgentActor` when a turn is due.
2. **`IAgentDefinitionProvider`** — resolves the agent id to an
   `AgentDefinition` (instructions + `AgentExecutionConfig`).
3. **`IAgentToolLauncher`** — one implementation per external tool; prepares
   the per-invocation working directory, env vars, and volume mounts.
4. **`IContainerRuntime`** — Docker/Podman driver the dispatcher uses to run
   (or start) the container the launcher described.
5. **A2A protocol** — how the dispatcher talks to the running container.
6. **MCP** — how the container calls back into the platform for tools.
7. **Dapr Conversation** (Dapr-Agent only) — the Dapr building block that
   routes the LLM call from inside the container to Ollama / OpenAI /
   Anthropic / Google.

The contract between the dispatcher and the launcher is intentionally narrow:
every tool-specific detail (Claude Code's `--resume` handshake, Codex's
auth.json layout, the Dapr Agent's MCP bridge) stays behind `IAgentToolLauncher`.

---

## 1. `A2AExecutionDispatcher`

Source: `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs`.

The dispatcher has two paths:

| Path          | Trigger                                                      | Container lifecycle                    |
| ------------- | ------------------------------------------------------------ | -------------------------------------- |
| **Ephemeral** | `AgentExecutionConfig.Hosting == Ephemeral` (default)        | One container per invocation.           |
| **Persistent**| `AgentExecutionConfig.Hosting == Persistent`                 | Long-lived service; shared across turns. |

Both paths share steps 1–3 (resolve → assemble prompt → issue MCP session) and
diverge at the container-runtime call. The persistent path is backed by
`PersistentAgentRegistry`, which tracks the container's health and re-launches
it on failure.

The dispatcher does **not** know the tool. It looks up the launcher by the
`AgentExecutionConfig.Tool` string in an `IDictionary<string, IAgentToolLauncher>`
populated from DI, and hands off to the launcher for prep.

```text
AgentActor.ExecuteTurn()
  → A2AExecutionDispatcher.DispatchAsync(message, context)
     → IAgentDefinitionProvider.GetByIdAsync(agentId)
     → IPromptAssembler.AssembleAsync(message, context)
     → IMcpServer.IssueSession(agentId, conversationId)
     → launcher.PrepareAsync(launchContext)          ── env + mounts + workdir
     → IContainerRuntime.RunAsync / StartAsync        ── the container runs
     → (persistent only) A2A client SendMessageAsync  ── talk to the container
     → launcher.CleanupAsync(workdir)                 ── scrub the workdir
     → BuildResponseMessage(…)
```

`AgentLaunchContext` — the record the dispatcher hands to the launcher — now
carries `Provider` and `Model` (both `string?`). The dispatcher reads them
from `AgentExecutionConfig` and forwards them unchanged; launchers that route
through Dapr Conversation use them to pin the Conversation component. Other
launchers ignore them.

---

## 2. Two launcher tiers

The OSS core ships two conceptually different launcher tiers. New launchers
slot into one or the other.

### Tier A — CLI-sidecar launchers

Examples: `ClaudeCodeLauncher`, `CodexLauncher`, `GeminiLauncher`.

The external tool is a CLI agent (Claude Code, Codex, Gemini CLI). The
launcher materialises the agent's on-disk workspace — API keys, system
prompt, resume-from-checkpoint state — and the container image's entrypoint
wraps the CLI in an A2A sidecar so the dispatcher can talk to it the same way
it talks to a native A2A service.

Key properties:

- One container per invocation (ephemeral).
- The A2A sidecar translates between A2A tasks and the CLI's stdin/stdout.
- No Dapr sidecar is involved; the LLM call is the CLI's own HTTP call to
  its provider.
- `Provider` / `Model` on `AgentLaunchContext` are ignored — the CLI owns
  provider selection via its own config file.

### Tier B — A2A-native launchers

Example: `DaprAgentLauncher` (tool identifier `"dapr-agent"`).

The container is itself an A2A service that runs a platform-managed agentic
loop (here, `dapr_agents.DurableAgent`). The container image:

- Exposes the A2A endpoint on `AGENT_PORT` (8999 by default).
- Resolves its tools dynamically at startup via the platform's MCP server.
- Routes its LLM calls through the **Dapr Conversation** building block so
  the concrete provider (Ollama, OpenAI, Anthropic, Google) is selected by
  YAML, not code.

Key properties:

- Can run ephemeral **or** persistent.
- The A2A server is part of the container, not a wrapper around a CLI.
- The Dapr Conversation component exposes the provider by **component name**
  (not component `type`). The Python agent passes the component name as the
  `llm` argument to `DurableAgent(...)` so mis-routed or unconfigured
  providers fail at startup instead of silently falling back to an
  environment default.

> **Historical note.** A component file named `conversation-ollama.yaml` may
> legitimately declare `type: conversation.openai` — Ollama exposes an
> OpenAI-compatible surface on `:11434/v1`, and the OpenAI Conversation
> component is happy to talk to it. What matters is that the Python agent
> picks the component by `metadata.name`, which is `llm-provider` for every
> provider YAML the repo ships. Changing providers is a swap of which
> `conversation-*.yaml` is active in the sidecar's `--resources-path`
> directory.

---

## 3. The A2A transport

Both tiers speak A2A 0.3.x. The dispatcher uses the SDK's `A2AClient` to send
one `SendMessageRequest` per turn:

```text
POST /                                     (on the container's :8999)
Content-Type: application/json
{
  "message": { "role": "user", "parts": [ { "text": "<prompt>" } ], … },
  "configuration": { "acceptedOutputModes": [ "text/plain" ] }
}
```

The response is either a terminal `Message` or an `AgentTask` whose
`artifacts` carry the final text. `A2AExecutionDispatcher.MapA2AResponseToMessage`
extracts the text and wraps it into the platform's internal
`Cvoya.Spring.Core.Messaging.Message` envelope.

**Failure handling.** A `TaskState.failed` in the response is a real
failure — it surfaces as `ExitCode: 1` in the outbound platform message and
is assertable in scenarios.

---

## 4. MCP callback channel

`IMcpServer` issues a short-lived per-invocation token and exposes
`McpEndpoint` — the URL the container can reach the platform on (typically
`http://host.docker.internal:<port>/mcp`). The launcher stamps both into the
container env (`SPRING_MCP_ENDPOINT`, `SPRING_AGENT_TOKEN`), and the agent
inside the container calls `tools/list` to discover callable skills and
`tools/call` to invoke them.

The session is revoked in the dispatcher's `finally` block for the ephemeral
path; persistent agents reuse a stable session id derived from the agent id.

See [Workflows](workflows.md) for the sidecar-protocol layer diagram.

---

## 5. Dapr Conversation wiring (Dapr-Agent only)

The `DaprAgentLauncher` forwards three YAML-driven knobs to the container:

| Env var                | Source (`AgentExecutionConfig`) | Purpose |
| ---------------------- | ------------------------------- | ------- |
| `SPRING_LLM_PROVIDER`  | `Provider` (default `ollama`)    | Dapr Conversation **component name** the agent binds to. Must match `metadata.name` in one of the YAMLs under `agents/dapr-agent/dapr/components/`. |
| `SPRING_MODEL`         | `Model` (default `OllamaOptions.DefaultModel`) | Model identifier the component requests. |
| `OLLAMA_ENDPOINT`      | `OllamaOptions.BaseUrl`          | Only used by the Ollama/OpenAI-compat component; other components ignore it. |

`agents/dapr-agent/agent.py` reads these three env vars and passes the
resolved values to `DurableAgent(...)` as `llm=<provider>`, `model=<model>`.

> **Sidecar status.** The OSS topology today ships the Python agent as a
> standalone A2A service. If `dapr_agents.DurableAgent` is configured against
> an OpenAI-compatible endpoint (Ollama's `/v1` surface), no Dapr sidecar is
> required at runtime because the SDK falls back to its own HTTP client.
> Adding a Dapr sidecar is a future hardening step when non-OpenAI-compatible
> providers are used — the `DaprSidecarManager` (already present) will mount
> `agents/dapr-agent/dapr/components/` at `/components` and run `daprd
> --resources-path /components --app-port 8999` alongside the agent.

---

## 6. The YAML contract

To make the provider/model configurable by YAML only, `AgentDefinition`'s
`execution` block accepts these fields:

```yaml
execution:
  tool: dapr-agent                          # selects the launcher
  image: localhost/spring-dapr-agent:latest # required for container-backed tools
  runtime: podman                           # optional runtime hint
  hosting: ephemeral                        # or "persistent"
  provider: ollama                          # Dapr Conversation component name
  model: llama3.2:3b                        # model identifier
```

Switching providers is a pure change to `provider` / `model` (and, if the
target provider isn't already present, adding the matching component YAML to
`agents/dapr-agent/dapr/components/`). No C# code change is required.

The runtime surfaces the definition to the platform through two paths:

- `spring agent create --definition-file <json>` on the CLI (JSON serialised
  form of the YAML above under an `execution` key).
- Direct HTTP to `POST /api/v1/agents` with `DefinitionJson` on the request
  body.

`DbAgentDefinitionProvider.ExtractExecution` is the single reader and it
accepts both the top-level `execution:` block and the legacy
`ai.environment:` block for back-compat.

---

## 7. Adding a new launcher

Checklist for a fresh `IAgentToolLauncher`:

1. Implement `IAgentToolLauncher` — pick a stable `Tool` identifier used in
   `execution.tool`.
2. Decide the tier:
   - Tier A (CLI wrapped in A2A sidecar) — stamp `SPRING_*` env vars the
     sidecar consumes, return a workspace mount.
   - Tier B (native A2A) — stamp `AGENT_PORT`, plus any tool-specific env.
3. Register with `services.AddSingleton<IAgentToolLauncher, YourLauncher>()`
   in `ServiceCollectionExtensions`.
4. If Dapr Conversation is involved, honour `AgentLaunchContext.Provider` and
   `.Model` rather than hard-coding a provider.
5. Add a `*LauncherTests` in `tests/Cvoya.Spring.Dapr.Tests/Execution/`.

The dispatcher auto-discovers launchers via DI and routes by the `Tool`
property.
