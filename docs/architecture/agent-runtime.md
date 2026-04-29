# Agent Runtime

> **[Architecture Index](README.md)** | Related: [Workflows](workflows.md), [Units & Agents](units.md), [Deployment](deployment.md), [Messaging](messaging.md)

---

> The implementation-neutral contract that downstream agent runtimes (in any language) implement is specified in [`docs/specs/agent-runtime-boundary.md`](../specs/agent-runtime-boundary.md). This document describes the Spring Voyage platform's implementation of that contract; an SDK in another language follows the spec, not this doc.

This document describes how the platform turns a single inbound message to an
`agent://` address into an actual agent turn. The layers, in order of
appearance on the dispatch path, are:

1. **`A2AExecutionDispatcher`** — the single entry point invoked by the
   `AgentActor` when a turn is due.
2. **`IAgentDefinitionProvider`** — resolves the agent id to an
   `AgentDefinition` (instructions + `AgentExecutionConfig`).
3. **`IAgentToolLauncher`** — one implementation per external tool; prepares
   the per-invocation working directory, env vars, and volume mounts.
4. **`IContainerRuntime`** — the execution-dispatcher's handle on the
   container runtime. In the worker process the binding is
   `DispatcherClientContainerRuntime`, which forwards every call over HTTP
   to the `spring-dispatcher` service. The dispatcher's own backend is
   `PodmanRuntime` (OSS) — this is the only process that holds the host
   container-runtime credentials. See
   [Deployment — Dispatcher service](deployment.md#dispatcher-service).
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

**Unit-inheritance merge (#601 B-wide).** The
`AgentExecutionConfig` the dispatcher receives is already merged with the
parent unit's `execution:` defaults. `DbAgentDefinitionProvider.GetByIdAsync`
reads the agent's own declared block, looks up the agent's parent unit (first
membership by `CreatedAt`), pulls the unit's persisted execution defaults
through `IUnitExecutionStore`, and runs a field-level precedence merge:

- Per field (`tool`, `image`, `runtime`, `provider`, `model`) — **agent wins** when the
  agent set the value; otherwise the unit default fills in; otherwise the
  field is null and the dispatcher fails cleanly with a merge-aware error
  message pointing operators at both surfaces.
- `hosting` is **agent-exclusive** — never inherits. A unit cannot change
  whether an agent is ephemeral or persistent.

See `docs/architecture/units.md § Unit execution defaults and the agent →
unit → fail resolution chain` for the full contract and the HTTP / CLI / portal
surfaces that edit the same persisted JSON the merge reads.

```text
AgentActor.ExecuteTurn()
  → A2AExecutionDispatcher.DispatchAsync(message, context)
     → IAgentDefinitionProvider.GetByIdAsync(agentId)
     → IPromptAssembler.AssembleAsync(message, context)
     → IMcpServer.IssueSession(agentId, threadId)
     → launcher.PrepareAsync(launchContext)          ── argv + env + mounts + workdir + stdin
     → ContainerConfigBuilder.Build(image, spec)     ── single seam to ContainerConfig
     → IContainerRuntime.StartAsync (detached)        ── ephemeral OR persistent: same call
     → poll GET /.well-known/agent.json on :A2APort  ── readiness probe (60s budget, 200ms backoff)
     → A2AClient.SendMessageAsync(SendMessageRequest) ── A2A roundtrip, both modes
     → MapA2AResponseToMessage(...)                   ── A2A response → Spring message
     → ephemeral: EphemeralAgentRegistry.ReleaseAsync ── teardown on turn drain
       persistent: leave running, registered in PersistentAgentRegistry
```

Both hosting modes share a single dispatch path. The only branch is the
post-roundtrip lifecycle decision: ephemeral tears down, persistent stays
running. The unification was decided in [ADR 0025](../decisions/0025-unified-agent-launch-contract.md)
and shipped through PRs 4–5 of the #1087 series, collapsing the legacy
"ephemeral goes through `RunAsync + harvest stdout`" branch onto this
unified path. The container's PID 1 is now always the agent-base bridge
(BYOI conformance paths 1/2 — see [ADR 0027](../decisions/0027-agent-image-conformance-contract.md))
or the agent runtime itself (path 3, native A2A); the platform no longer
launches containers whose entrypoint is a "wait forever" stub. Container
scope is per-agent, not per-unit — see [ADR 0026](../decisions/0026-per-agent-container-scope.md).

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

## 4a. Skill registries

See [ADR 0014](../decisions/0014-skill-invoker-seam.md) for the decision record behind the `ISkillInvoker` seam and the expertise-directory-driven skill surface.

Tools exposed over MCP are surfaced by any number of `ISkillRegistry`
implementations registered in DI. The MCP server enumerates every registry at
`tools/list` time and routes every `tools/call` to the registry that declared
the tool. Two registries ship in the OSS core:

| Registry                    | Source | Surface |
| --------------------------- | ------ | ------- |
| `GitHubSkillRegistry`        | GitHub connector package | Hand-rolled tool definitions for GitHub operations (issues, PRs, labels, topology). |
| `ExpertiseSkillRegistry`     | Core (#359)              | **Expertise-directory-driven**: skills are derived live from `IExpertiseAggregator` (per #487 / #498) and projected through the caller's `BoundaryViewContext` (per #497). No startup snapshot — a mutation (agent gains expertise, unit boundary changes) propagates on the next enumeration. |

### The expertise-directory-driven skill surface (#359)

The skill surface is a **projection of the expertise directory**, not of the
agent roster. Concretely:

1. **Source of truth.** `IExpertiseSkillCatalog` reads aggregated expertise
   through `IExpertiseAggregator` — the same interface that serves every
   other expertise read. There is no parallel capability registry to keep in
   sync.
2. **Typed-contract eligibility.** Only `ExpertiseDomain` entries with a
   non-null `InputSchemaJson` are surfaced as skills. A consultative-only
   entry (free-form advice, no structured request shape) leaves the schema
   null and stays message-only.
3. **Boundary projection.** External callers see only unit-projected entries
   (`origin = unit://…`). Agent-level expertise inside a unit that isn't
   covered by a projection is hidden from the outside and visible only to
   callers already inside the boundary. The catalog applies the boundary in
   two ways: by asking the aggregator for the caller-aware view, and by
   filtering non-unit origins out of external enumerations as a defence in
   depth.
4. **Naming scheme.** Skill names follow `expertise/{slug}` where the slug is
   a case-folded, path-safe projection of the domain name (see
   `ExpertiseSkillNaming`). Agent names never appear in the skill surface —
   swapping the agent that holds an expertise entry does NOT rename the
   skill, and the catalog is stable across agent churn.
5. **Live resolution.** Every enumeration hits the aggregator. The
   aggregator's cache + `InvalidateAsync` contract from #487 handles the
   freshness story; the registry's `GetToolDefinitions()` re-fetches on every
   call (with the last-enumerated snapshot returned while the refresh is in
   flight, since the `ISkillRegistry` method is synchronous).

### The `ISkillInvoker` seam

Skill callers — planners, the MCP server, any future A2A gateway — never
reach into `IMessageRouter` directly. Instead they invoke through
`ISkillInvoker`:

```text
caller → ISkillInvoker.InvokeAsync(SkillInvocation)
         → catalog.ResolveAsync(name, caller's BoundaryViewContext)
         → build Message(to = catalog target, from = caller)
         → IMessageRouter.RouteAsync(message)         ── boundary + permission + policy + activity
         → translate response payload back to SkillInvocationResult
```

Routing through `IMessageRouter` is load-bearing: that is the single
enforcement seam for boundary opacity (#413 / #497), hierarchy permissions
(#414), cloning policy (#416), initiative levels (#415), and activity
emission (#391 / #484). Bypassing the router would make the skill surface a
governance hole.

A **second, invocation-time boundary re-check** is performed by the invoker:
the catalog's `ResolveAsync` takes the caller's `BoundaryViewContext`, so a
skill the caller cannot see is impossible to call even when the caller knows
the name. Combined with the router's permission chain this gives defence in
depth.

### Alternative invoker implementations

`ISkillInvoker` is the extension seam that will host the A2A message gateway
tracked in [#539](https://github.com/cvoya-com/spring-voyage/issues/539).
The gateway will register an alternative implementation that translates a
`SkillInvocation` to an outbound A2A call instead of an internal `Message`;
callers do not change. The default `MessageRouterSkillInvoker` is registered
with `TryAdd*` so a downstream host (private cloud, integration test harness)
can pre-register its own and keep it.

---

## 4b. Provider applies only to Dapr Agent

The `execution.provider` and `execution.model` fields are **only consumed by
the `dapr-agent` launcher**. The Tier-A CLI launchers (Claude Code, Codex,
Gemini CLI) hardcode their provider inside the tool binary — that's the
defining property of a CLI-sidecar launcher. Setting `provider` on an agent
targeting one of those tools has no runtime effect.

| Execution tool | What provider the tool calls | Honours `AgentExecutionConfig.Provider`? |
|----------------|------------------------------|------------------------------------------|
| `claude-code`  | Anthropic (hardcoded in the Claude Code CLI) | No |
| `codex`        | OpenAI (hardcoded in the Codex CLI) | No |
| `gemini`       | Google Gemini (hardcoded in the Gemini CLI) | No |
| `dapr-agent`   | Whichever Dapr Conversation component the `provider` value names (`ollama`, `openai`, `anthropic`, `googleai`) | Yes |
| `custom`       | Undefined — the custom launcher owns its own contract | Only when the custom launcher declares it |

**Surface-level consequence (#598):**

- The unit-creation wizard and the CLI only accept `--provider` / `--model`
  when `--tool=dapr-agent`. They are rejected with a targeted error
  message for the other tools so operators don't discover at dispatch
  time that the flag had no effect.
- A custom tool that wants to surface a Provider selector must declare
  that explicitly — either by shipping its own wizard step that advertises
  the provider axis (following the connector-wizard pattern) or by
  documenting the semantic in its launcher's doc-comment. The platform's
  create-unit UI does not expose a generic Provider dropdown for custom
  tools because the contract is undefined.
- Credentials for the `dapr-agent` launcher's provider flow through the
  tier-2 tenant-default resolver (`ILlmCredentialResolver`, #615). The
  portal's create wizard no longer gates on credential validation at
  accept time (removed in #941 — the inline banner went with it); unit
  creation now flows straight into `Validating`, and the detail page's
  Validation panel (`src/Cvoya.Spring.Web/src/components/units/detail/validation-panel.tsx`)
  owns the operator-facing feedback. Tenant-default vs unit-override
  resolution for credentials is still authoritative and surfaces
  verbatim on the Execution / Secrets tabs; see
  [docs/guide/portal.md](../guide/user/portal.md) for the detail page walkthrough.

Future changes to this matrix — e.g. a "Claude Code with Vertex AI
backend" tool that legitimately takes a provider axis — should update
this table and drop the wizard gate on that specific tool.

---

## 5. Dapr Conversation wiring (Dapr-Agent only)

> **Naming disambiguation.** "Conversation" in this section refers to Dapr's [Conversation API](https://docs.dapr.io/reference/components-reference/supported-conversation/) — the building block that abstracts the LLM provider call (Ollama / OpenAI / Anthropic / Google). It is unrelated to Spring Voyage's **Thread** concept (the participant-set relationship described in [`docs/architecture/thread-model.md`](thread-model.md) and [ADR-0030](../decisions/0030-thread-model.md)). The `SPRING_LLM_PROVIDER` env var binds to a Dapr Conversation **component name**, not to a Spring Voyage thread.

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
  image: localhost/spring-voyage-agent-dapr:latest # required for container-backed tools
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

## 7. BYOI conformance contract

Operators (OSS and Cloud) frequently want to bring their own agent images — pre-baked with proprietary CLIs, custom system tooling, an internal trust anchor, or a non-Debian distro. The contract between an agent image and `A2AExecutionDispatcher` is small enough to fit on one screen, and there are three conformance paths to satisfy it. [ADR 0027](../decisions/0027-agent-image-conformance-contract.md) is the canonical reference; this section is the operational summary. For a step-by-step guide with copy-pasteable Dockerfile snippets, the full `SPRING_*` env contract, version compatibility rules, and debugging tips, see [`docs/guide/byoi-agent-images.md`](../guide/operator/byoi-agent-images.md).

### The wire contract

An image conforms when the running container, after launch by the dispatcher, exposes:

- A2A 0.3.x at `http://0.0.0.0:${AGENT_PORT}/` (default `8999`, set by the launcher via `AgentLaunchSpec.A2APort`).
- An Agent Card at `GET /.well-known/agent.json` whose `protocolVersion` is `"0.3"`.
- A response header `x-spring-voyage-bridge-version: <semver>` on every response (and the same field on the Agent Card / task payload). The dispatcher logs version skew so operators can correlate odd behaviour with stale sidecars.
- Implementations of A2A `message/send`, `tasks/cancel`, and `tasks/get`.
- Honouring the launcher-supplied environment, including any `SPRING_*` keys the launcher stamped into `AgentLaunchSpec.EnvironmentVariables`.

### The three paths

| Path | Recipe                                                                                                                                              | When to pick it                                                                                                |
| ---- | --------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| 1    | `FROM ghcr.io/cvoya-com/agent-base:<semver>` and `RUN`-install your CLI tool. ENTRYPOINT is left as-is — the bridge runs on `:8999` automatically.       | Default. Fastest path. Works for anything that can run on Debian 12 + Node 22.                                  |
| 2    | Pull the bridge into a custom base. Either `npm install -g @cvoya/spring-voyage-agent-sidecar` (Node-bearing image), or copy the static binary from each GitHub Release (`spring-voyage-agent-sidecar-linux-amd64`, `linux-arm64`, `darwin-arm64`) into a Node-less image. Set the binary as the `ENTRYPOINT`. | You need a non-Debian distro, a rootless image with non-default UIDs, or you can't have Node in the runtime layer. |
| 3    | Implement A2A 0.3.x natively in your image. No bridge involved. The launcher must speak directly to your endpoint.                                  | You already speak A2A natively (e.g., the Python Dapr Agent at `DaprAgentLauncher`).                            |

The Tier B native launcher (`DaprAgentLauncher`) is the canonical example of path 3. The Tier A launchers (`ClaudeCodeLauncher`, `CodexLauncher`, `GeminiLauncher`) all use path 1 by default.

### Versioning commitment

- The bridge npm package and the OCI tag use semver.
- N-2 backward compatibility on the bridge package — a worker dialing this bridge accepts versions within the last 2 majors.
- A2A pinned to `0.3.x`. A bump to `0.4.x` or `1.x` is a deliberate breaking change with a deprecation window on the dispatcher side.
- The bridge source lives in the same repository as the dispatcher, under [`deployment/agent-sidecar/`](../../deployment/agent-sidecar). Releases are cut on tags shaped `agent-base-vX.Y.Z`.

### Local verification

```bash
deployment/build-sidecar.sh                          # builds ghcr.io/cvoya-com/agent-base:dev
docker run --rm -p 8999:8999 \
  -e SPRING_AGENT_ARGV='["true"]' \
  ghcr.io/cvoya-com/agent-base:dev &

curl -s http://localhost:8999/.well-known/agent.json | jq '.protocolVersion, .version'
curl -s -X POST http://localhost:8999/ \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","method":"message/send","params":{"message":{"parts":[{"text":"ping"}]}},"id":1}'
```

The first command should print `"0.3"` and the bridge semver; the second should return a JSON-RPC `result` whose `status.state` is `completed`.

---

## 8. Adding a new launcher

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
