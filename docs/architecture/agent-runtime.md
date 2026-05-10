# Agent Runtime

> **[Architecture Index](README.md)** | Related: [Workflows](workflows.md), [Units](units.md), [Agents](agents.md), [Expertise](expertise.md), [Deployment](deployment.md), [Messaging](messaging.md)

---

> The implementation-neutral contract that downstream agent runtimes (in any language) implement is specified in [`docs/specs/agent-runtime-boundary.md`](../specs/agent-runtime-boundary.md). This document describes the Spring Voyage platform's implementation of that contract; an SDK in another language follows the spec, not this doc.

This document describes how the platform turns a single inbound message to an
`agent:<id>` address into an actual agent turn. The vocabulary is the
three-concept split from [ADR-0038](../decisions/0038-agent-runtime-and-model-provider-split.md):

- **AgentRuntime** — the in-container execution engine. Closed set in v0.1: `claude-code`, `codex`, `gemini`, `spring-voyage`.
- **ModelProvider** — the company whose API hosts a set of LLMs. Open set: `anthropic`, `openai`, `google`, `ollama`, future additions.
- **Model** — a specific LLM, identified by the structured pair `{provider, id}`. The provider is intrinsic to the model.

The user-facing execution config is the tuple `(runtime, model)`. There is no
separate `provider` axis in the manifest, in the wire shape, or stored on a
tenant install row — the provider is read off `model.provider`.

The layers, in order of appearance on the dispatch path, are:

1. **`A2AExecutionDispatcher`** — the single entry point invoked by the
   `AgentActor` when a turn is due.
2. **`IAgentDefinitionProvider`** — resolves the agent id to an
   `AgentDefinition` (instructions + `AgentExecutionConfig`).
3. **`IAgentRuntimeLauncher`** — one strategy per agent runtime; prepares the
   per-invocation working directory, env vars, and volume mounts. Strategies
   are looked up by the `launcher` id on the runtime's
   [`runtime-catalog.yaml`](#6-the-runtime-catalogue) entry.
4. **`IContainerRuntime`** — the execution-dispatcher's handle on the
   container runtime. In the worker process the binding is
   `DispatcherClientContainerRuntime`, which forwards every call over HTTP
   to the `spring-dispatcher` service. The dispatcher's own backend is
   `PodmanRuntime` (OSS) — this is the only process that holds the host
   container-runtime credentials. See
   [Deployment — Dispatcher service](deployment.md#dispatcher-service).
5. **A2A protocol** — how the dispatcher talks to the running container.
6. **MCP** — how the container calls back into the platform for tools.
7. **Dapr Conversation** (Spring Voyage Agent only) — the Dapr building block
   that routes the LLM call from inside the container to the configured
   model provider (Ollama / OpenAI / Anthropic / Google).

The contract between the dispatcher and the launcher is intentionally narrow:
every runtime-specific detail (Claude Code's `--resume` handshake, Codex's
auth.json layout, the Spring Voyage Agent's MCP bridge) stays behind
`IAgentRuntimeLauncher`.

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

The dispatcher does **not** accept a per-agent container runtime. It reads the
agent's `AgentExecutionConfig.AgentRuntimeId` (sourced from the `ai.runtime`
manifest field, or the wire `runtime` / persisted `agent` execution slot),
looks up the matching `AgentRuntime` entry in the runtime catalogue, and
dispatches the turn through the `IAgentRuntimeLauncher` registered under the
runtime entry's `launcher` id (#1732). The launcher then handles prep. The
docker/podman binary is host-level platform configuration per ADR-0039 §7.

**Unit-inheritance merge (#601 B-wide).** The
`AgentExecutionConfig` the dispatcher receives is already merged with the
parent unit's `execution:` defaults. `DbAgentDefinitionProvider.GetByIdAsync`
reads the agent's own declared block, looks up the agent's parent unit (first
membership by `CreatedAt`), pulls the unit's persisted execution defaults
through `IUnitExecutionStore`, and runs a field-level precedence merge:

- Per field (`agent`, `image`, `model.provider`, `model.id`) — **agent wins**
  when the agent set the value; otherwise the
  unit default fills in; otherwise the field is null and the dispatcher fails
  cleanly with a merge-aware error message pointing operators at both
  surfaces.
- `hosting` is **agent-exclusive** — never inherits. A unit cannot change
  whether an agent is ephemeral or persistent.

See [ADR-0039 § 6](../decisions/0039-units-are-agents.md#6-inheritance-top-level-under-tenant-multi-parent-override-reparenting-validation)
for the current inheritance contract, including top-level tenant defaults,
single-parent inheritance, multi-parent conflict validation, and reparenting
checks.

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

`AgentLaunchContext` — the record the dispatcher hands to the launcher —
carries the resolved `(runtime, provider, modelId)` tuple. The dispatcher
reads them from `AgentExecutionConfig` and forwards them unchanged; launchers
that route through Dapr Conversation use `provider` and `modelId` to pin the
Conversation component and the model. CLI-sidecar launchers ignore them
because their CLIs hardcode the provider.

---

## 2. Two launcher tiers

The OSS core ships two conceptually different launcher tiers. New launchers
slot into one or the other.

### Tier A — CLI-sidecar launchers

Examples: `ClaudeCodeLauncher`, `CodexLauncher`, `GeminiLauncher`.

The agent runtime wraps a CLI agent (Claude Code, Codex, Gemini CLI). The
launcher materialises the agent's on-disk workspace — API keys, system
prompt, resume-from-checkpoint state — and the container image's entrypoint
wraps the CLI in an A2A sidecar so the dispatcher can talk to it the same way
it talks to a native A2A service.

Key properties:

- One container per invocation (ephemeral).
- The A2A sidecar translates between A2A tasks and the CLI's stdin/stdout.
- No Dapr sidecar is involved; the LLM call is the CLI's own HTTP call to
  its provider.
- These runtimes are **fixed-provider** in the runtime catalogue — each one
  declares exactly one `modelProviders:` entry, so the wizard hides the
  provider picker (see [ADR-0038 § 1](../decisions/0038-agent-runtime-and-model-provider-split.md#1-three-concepts-one-tuple-at-the-user-facing-surface)).

### Tier B — A2A-native launchers

Example: `SpringVoyageAgentLauncher` (runtime id `spring-voyage`).

The container is itself an A2A service that runs a platform-managed agentic
loop using `dapr_agents`. The container image:

- Exposes the A2A endpoint on `AGENT_PORT` (8999 by default).
- Resolves its tools dynamically at startup via the platform's MCP server.
- Routes its LLM calls through the **Dapr Conversation** building block so
  the concrete provider (Ollama, OpenAI, Anthropic, Google) is selected by
  YAML, not code.

Key properties:

- Can run ephemeral **or** persistent.
- The A2A server is part of the container, not a wrapper around a CLI.
- The Dapr Conversation component exposes the provider by **component name**
  (not component `type`). The Python agent passes the component name to
  `DaprChatClient` so mis-routed or unconfigured providers fail at startup
  instead of silently falling back to an environment default.
- This runtime is **multi-provider** in the runtime catalogue — its
  `modelProviders:` list carries `anthropic`, `openai`, `google`, `ollama`.
  The wizard surfaces the provider picker as a model-list filter, but the
  selected provider is recorded only as `model.provider`; there is no
  separate `provider` slot on the unit / agent (ADR-0038).

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
container env (`SPRING_MCP_URL`, `SPRING_MCP_TOKEN`), and the agent
inside the container calls `tools/list` to discover callable skills and
`tools/call` to invoke them.

The session is revoked in the dispatcher's `finally` block for the ephemeral
path; persistent agents reuse a stable session id derived from the agent id.

See [Workflows](workflows.md) for the sidecar-protocol layer diagram.

---

## 4a. Orchestration callback bootstrap

Every built-in launcher stamps the dispatcher orchestration callback bootstrap
into the runtime container:

| Env var | Source | Purpose |
| --- | --- | --- |
| `SPRING_CALLBACK_URL` | Worker `Dispatcher:BaseUrl` | Dispatcher base URL the Agent SDK uses to reach callback APIs. |
| `SPRING_CALLBACK_TOKEN` | Per-invocation callback JWT | Authenticates callbacks and scopes them to `(tenantId, agentAddress, threadId, messageId)`. |

The launcher path uses the same callback-token contract the dispatcher
validates for ADR-0039 D12/D13. Ephemeral launches receive a token for the
inbound message being served. Persistent containers receive launch-time
bootstrap credentials; per-message refresh for already-running persistent
containers is tracked separately in [#1943](https://github.com/cvoya-com/spring-voyage/issues/1943).
CLI-sidecar launchers append `/v1/runtime/orchestration` when configuring the
orchestration MCP server; the raw env var remains the dispatcher base URL for
SDK clients.

---

## 4b. Orchestration-tool surface

ADR-0039 closes the orchestration surface to five platform-provided tools. The
runtime path resolves these tools when the invoked agent has children and passes
the descriptors to the selected launcher through
`AgentLaunchContext.OrchestrationTools`. A leaf agent receives an empty tool
set. Unit operators and runtime authors do not enable a separate orchestration
mode.

| Tool | Description |
| --- | --- |
| `list_children` | Returns the address array of the unit's current direct members. No `OrchestrationDecision` event. |
| `inspect_child` | Returns metadata (`scheme`, `id`) for a specific child. No `OrchestrationDecision` event. |
| `delegate_to_child` | Dispatches the inbound message to a single child and returns the child's reply. Emits `OrchestrationDecision` with `Kind=Delegate`. |
| `fanout_to_children` | Dispatches to all children, or to a filtered set of children, and returns all replies. Emits `OrchestrationDecision` with `Kind=Fanout`. |
| `query_child_status` | Queries in-flight status of a prior dispatch. No `OrchestrationDecision` event. |

The runtime decides whether to answer directly, inspect children, delegate to
one child, or fan out to several children. The platform supplies the tools,
checks the call, routes the resulting child messages, and records delegation
evidence.

The same five tools are exposed through two runtime-facing surfaces:

| Surface | Runtime style | Runtimes | Attachment |
| --- | --- | --- | --- |
| MCP / env-var-keyed tool calls | LLM-driven | `spring-voyage`, `claude-code`, `codex`, `gemini` | The launcher injects orchestration tool definitions into the runtime's tool-call surface. `spring-voyage` receives the descriptor array through `SPRING_ORCHESTRATION_TOOLS`; the CLI runtimes receive a Spring orchestration MCP server. |
| Typed HTTP callback SDK | Workflow-driven | Runtime images using `Cvoya.Spring.AgentSdk` | The launcher stamps `SPRING_CALLBACK_URL` and `SPRING_CALLBACK_TOKEN` into the container environment. The SDK discovers those values and exposes typed `IOrchestrationClient` methods over the dispatcher's callback API. |

Both surfaces dispatch to the same platform-side
[`OrchestrationToolHandlers`](../../src/Cvoya.Spring.Dapr/Orchestration/OrchestrationToolHandlers.cs)
implementation and produce the same `OrchestrationDecision` evidence. The
SDK surface lives in
[`src/Cvoya.Spring.AgentSdk/`](../../src/Cvoya.Spring.AgentSdk/) and is
documented in [Agent SDK](agent-sdk.md). The tool-call surface — closed
enum, descriptor / schema shape, and per-runtime attachment mechanism — is
documented in [Orchestration Tools](orchestration-tools.md), the
runtime-image author contract.

See [ADR-0039 section 3](../decisions/0039-units-are-agents.md#3-children-are-exposed-as-orchestration-tools-to-the-runtime).

## 4c. Launcher's tool-attachment responsibility

Each per-runtime launcher is responsible for attaching the orchestration tool
surface before handing off to the runtime image. The runtime path resolves the
available tools before it calls the launcher. The source of truth is
`IOrchestrationToolProvider.GetOrchestrationTools(...)`, which takes the
invoked address and thread id and returns an `OrchestrationToolDescriptor[]`
with the closed tool name plus input and output JSON Schemas.

`AgentLaunchContext.OrchestrationTools` carries that descriptor array into the
selected `IAgentRuntimeLauncher`. Each launcher then follows one or both
attachment paths:

- **LLM-driven runtimes.** The launcher injects the orchestration tool
  definitions into the runtime's tool-call surface. For `spring-voyage`, this
  is the env-var-keyed descriptor list. For `claude-code`, `codex`, and
  `gemini`, this is an orchestration MCP server alongside the normal platform
  MCP server.
- **SDK-driven runtimes.** The launcher stamps `SPRING_CALLBACK_URL` and
  `SPRING_CALLBACK_TOKEN` into the container environment. `Cvoya.Spring.AgentSdk`
  reads them through `SpringAgent.FromEnvironment()` and calls the dispatcher's
  typed HTTP callback API.

| Runtime | Attachment mechanism |
| --- | --- |
| `spring-voyage` | Serialises the descriptors into `SPRING_ORCHESTRATION_TOOLS`; the runtime can invoke the handlers through Dapr actor-backed platform calls. |
| `claude-code` | Adds a Spring orchestration MCP server alongside the normal platform MCP server. |
| `codex` | Adds the same Spring orchestration MCP surface using Codex's MCP configuration. |
| `gemini` | Adds the same Spring orchestration MCP surface using Gemini CLI's tool configuration. |

Custom launchers use their runtime's own extension mechanism, but the abstract
tool names and JSON Schemas stay the same. This keeps the platform contract
uniform while allowing each runtime image to expose tools through its native
interface.

This responsibility builds on the launcher contract from
[ADR-0038](../decisions/0038-agent-runtime-and-model-provider-split.md) and the
tool surface from [ADR-0039 section 3](../decisions/0039-units-are-agents.md#3-children-are-exposed-as-orchestration-tools-to-the-runtime).

## 4d. `OrchestrationDecision` event shape

When the runtime calls a delegation tool, the platform publishes a
`DecisionMade` activity event. The durable payload is the Core
`OrchestrationDecision` record. The normalized event shape is:

```json
{
  "decisionId": "<uuid>",
  "tenantId": "<uuid>",
  "unitAddress": {
    "scheme": "<scheme>",
    "id": "<uuid>"
  },
  "threadId": "<uuid>",
  "inputMessageId": "<uuid>",
  "kind": "Delegate | Fanout | Inspect | NoOp",
  "targets": [
    {
      "scheme": "<scheme>",
      "id": "<uuid>"
    }
  ],
  "status": "Accepted | Routed | Failed",
  "resultMessageIds": ["<uuid>"],
  "reason": "<optional string>",
  "metadata": null,
  "createdAt": "<ISO-8601>"
}
```

`delegate_to_child` emits `Kind=Delegate`. `fanout_to_children` emits
`Kind=Fanout`. `list_children`, `inspect_child`, and `query_child_status` are
read-only probes and do not emit `OrchestrationDecision` events. `Kind=Inspect`
and `Kind=NoOp` remain part of the domain enum for explicit decision evidence,
but none of the five current handlers emit them. `Reason` is plain text supplied
by the runtime's tool call; it is never hidden model reasoning.

Subscribers consume this stream as delegation evidence. For example, the
GitHub connector's label-roundtrip subscriber listens for routed
`Delegate` decisions and applies connector-side label rules from the unit's
GitHub binding.

See [ADR-0039 section 4](../decisions/0039-units-are-agents.md#4-orchestration-decisions-are-first-class-evidence).

---

## 4e. Skill registries

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
   (`origin = unit:<id>`). Agent-level expertise inside a unit that isn't
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

## 4f. Provider surfaces vs. fixed-provider runtimes

The wizard rule from [ADR-0038 § 1](../decisions/0038-agent-runtime-and-model-provider-split.md#1-three-concepts-one-tuple-at-the-user-facing-surface)
is keyed off whether a runtime declares one or many `modelProviders` in the
catalogue:

| Runtime           | `modelProviders` in catalogue | Provider picker visibility |
|-------------------|-------------------------------|----------------------------|
| `claude-code`     | `[anthropic]`                 | Hidden (fixed)             |
| `codex`           | `[openai]`                    | Hidden (fixed)             |
| `gemini`          | `[google]`                    | Hidden (fixed)             |
| `spring-voyage`   | `[anthropic, openai, google, ollama]` | Shown (filter)     |

**Surface-level consequence:**

- The unit-creation wizard and the CLI accept `model.provider` only when the
  resolved runtime declares more than one provider. Specifying a provider on
  a fixed-provider runtime is rejected with a targeted error message so
  operators don't discover at dispatch time that the field had no effect.
- A custom runtime that wants to surface a provider selector declares its
  allowed providers in its `modelProviders:` catalogue entry. The platform's
  create-unit UI derives the picker visibility from `modelProviders.Count > 1`
  uniformly; there is no per-runtime UI special-casing.
- Credentials flow through `ILlmCredentialResolver` keyed on
  `(tenant, provider, authMethod)` per [ADR-0038 § 4](../decisions/0038-agent-runtime-and-model-provider-split.md#4-credential-matrix-is-derived-from-runtime-catalogyaml).
  Unit-level inheritance follows ADR-0003. The portal's create wizard does
  not gate on credential validation at accept time (removed in #941); unit
  creation flows straight into `Validating`, and the detail page's
  Validation panel
  (`src/Cvoya.Spring.Web/src/components/units/detail/validation-panel.tsx`)
  owns the operator-facing feedback.

Future evolution — for example a "Claude Code with Vertex AI backend"
runtime — drops the second provider into `claude-code`'s `modelProviders:`
list and the wizard rule does the rest, no per-runtime code change required.

---

## 5. Dapr Conversation wiring (Spring Voyage Agent runtime only)

> **Naming disambiguation.** "Conversation" in this section refers to Dapr's [Conversation API](https://docs.dapr.io/reference/components-reference/supported-conversation/) — the building block that abstracts the LLM provider call (Ollama / OpenAI / Anthropic / Google). It is unrelated to Spring Voyage's **Thread** concept (the participant-set relationship described in [`docs/architecture/thread-model.md`](thread-model.md) and [ADR-0030](../decisions/0030-thread-model.md)).

The `SpringVoyageAgentLauncher` forwards three YAML-driven knobs to the container:

| Env var                | Source (`AgentExecutionConfig`)         | Purpose |
| ---------------------- | --------------------------------------- | ------- |
| `SPRING_LLM_PROVIDER`  | `Model.Provider` (default `ollama`)     | Provider id label, used for telemetry / agent-card description. |
| `SPRING_MODEL`         | `Model.Id` (default `OllamaOptions.DefaultModel`) | Model identifier the component requests. |
| `SPRING_LLM_COMPONENT` | `llm-{provider}` (computed)             | Dapr Conversation **component name** the agent binds to. Per [ADR-0038 § 3](../decisions/0038-agent-runtime-and-model-provider-split.md#3-modelproviders-are-platform-configuration-alongside-agentruntimes-in-runtime-catalogyaml), in-tree Dapr component files live at `dapr/components/llm-{provider.id}.yaml` with `metadata.name: llm-{provider.id}`. |

`agents/spring-voyage-agent/agent.py` reads `SPRING_LLM_COMPONENT` and passes
the resolved name to `DaprChatClient(component_name=...)`. The model id is
configured on the Dapr component metadata; `SPRING_MODEL` is kept on the
container env for telemetry and agent-card rendering.

**Dapr component naming convention.** Each provider has one in-tree YAML at
`dapr/components/delegated-spring-voyage-agent/llm-{provider.id}.yaml`:

| Provider    | YAML file              | `metadata.name` | Dapr `type` |
|-------------|------------------------|-----------------|-------------|
| `anthropic` | `llm-anthropic.yaml`   | `llm-anthropic` | `conversation.anthropic` |
| `openai`    | `llm-openai.yaml`      | `llm-openai`    | `conversation.openai` |
| `google`    | `llm-google.yaml`      | `llm-google`    | `conversation.googleai` |
| `ollama`    | `llm-ollama.yaml`      | `llm-ollama`    | `conversation.openai` (Ollama exposes an OpenAI-compatible surface) |

The Dapr `type:` field stays in the `conversation.<provider>` namespace
because that is Dapr's contract for the Conversation building block, not
ours. The `metadata.name` is the platform's contract — that is the name
`SPRING_LLM_COMPONENT` resolves to.

> **Sidecar status.** The OSS topology today ships the Python agent as a
> standalone A2A service. The `DaprSidecarManager` mounts the components
> directory at `/components` and runs `daprd --resources-path /components
> --app-port 8999` alongside the agent so the credential-bearing components
> resolve at first use.

---

## 6. The runtime catalogue

The runtimes the platform supports — and each runtime's allowed providers,
per-edge auth methods, thread-binding mechanism, and prompt-injection mode —
live as data in `platform/runtime-catalog.yaml`. There are no per-runtime or
per-provider C# classes; per-wire-format behaviour is encoded in a small set
of `IModelProviderAdapter` strategies (`anthropic`, `openai-compatible`,
`google`) and per-runtime behaviour in `IAgentRuntimeLauncher` strategies
(`claude-code-cli`, `codex-cli`, `gemini-cli`, `spring-voyage-agent`). Both
strategy registries dispatch by id read off the catalogue entry. See
[ADR-0038 § 2 + § 3](../decisions/0038-agent-runtime-and-model-provider-split.md#2-agentruntimes-are-platform-configuration-not-per-runtime-classes)
for the rationale and the full schema.

The user-facing manifest selects the runtime via the top-level `ai:` block:

```yaml
ai:
  runtime: spring-voyage
  model:
    provider: ollama
    id: llama3.2:3b
execution:
  image: ghcr.io/cvoya-com/spring-voyage-agent:latest # required for container-backed runtimes
  hosting: ephemeral                          # or "persistent"
```

For a fixed-provider runtime:

```yaml
ai:
  runtime: claude-code
  model:
    provider: anthropic
    id: claude-opus-4-7
```

`ai.runtime` names a catalogue entry; the dispatcher looks up the launcher
strategy by the entry's `launcher` id. Pre-ADR-0038 manifests carrying
`ai.agent`, flat `execution.provider`, `execution.runtime`, or `execution.tool`
are rejected by `ManifestParser` with a precise error pointing at the new
shape.

The runtime surfaces the definition to the platform through two paths:

- `spring agent create --definition-file <json>` on the CLI (JSON serialised
  form of the YAML above under `execution` and `ai` keys).
- Direct HTTP to `POST /api/v1/agents` with `DefinitionJson` on the request
  body.

`DbAgentDefinitionProvider.ExtractExecution` is the single reader.

---

## 7. The credential matrix

A provider declares the auth methods it accepts (`authMethods` on the
provider entry). Each agent runtime declares, per provider it can dispatch
to, the single auth method it consumes (`authMethod` on the `modelProviders[]`
edge). The runtime × provider × authMethod matrix is the projection of these
two pieces of config — the catalogue is the single source of truth.

`ILlmCredentialResolver` is keyed on `(tenant, provider, authMethod)` with
unit-level inheritance carried forward per ADR-0003. The launcher at dispatch
time:

1. Reads the resolved provider id off `AgentExecutionConfig.Model.Provider`.
2. Looks up the (runtime, provider) edge in the catalogue to learn the
   `authMethod` and `credentialEnvVar`.
3. Calls `ILlmCredentialResolver.ResolveAsync(provider, authMethod, agentId, unitId)`.
4. Stamps the resolved value into the container env under `credentialEnvVar`.

A provider with an empty `authMethods` list (Ollama in v0.1) requires no
credential — the launcher skips resolution for that edge.

---

## 8. BYOI conformance contract

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
| 3    | Implement A2A 0.3.x natively in your image. No bridge involved. The launcher must speak directly to your endpoint.                                  | You already speak A2A natively (e.g., the Python Dapr Agent at `SpringVoyageAgentLauncher`).                            |

The Tier B native launcher (`SpringVoyageAgentLauncher`) is the canonical example of path 3. The Tier A launchers (`ClaudeCodeLauncher`, `CodexLauncher`, `GeminiLauncher`) all use path 1 by default. The four `spring-voyage-agent-oss-{software-engineering,design,product-management,program-management}` images that back the **Spring Voyage OSS** dogfooding template (`packages/spring-voyage-oss/`) are additional path-1 derivatives — each `FROM ghcr.io/cvoya-com/spring-voyage-agent-base:<semver>`, installs the Claude Code CLI, and adds a role-specific toolchain on top, inheriting the bridge ENTRYPOINT unchanged. See [`docs/decisions/0034-oss-dogfooding-unit.md`](../decisions/0034-oss-dogfooding-unit.md) for the image strategy rationale.

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

## 9. Adding a new launcher

Checklist for a fresh `IAgentRuntimeLauncher`:

1. Add a `runtime-catalog.yaml` entry for the new runtime: stable `id`,
   `displayName`, `defaultImage`, `launcher` strategy id, `threadBinding`,
   `systemPromptInjection`, and the `modelProviders:` list with per-edge
   `authMethod` + `credentialEnvVar`.
2. Implement `IAgentRuntimeLauncher` and register it under the same
   `launcher` id used in the catalogue entry.
3. Decide the tier:
   - Tier A (CLI wrapped in A2A sidecar) — stamp `SPRING_*` env vars the
     sidecar consumes, return a workspace mount.
   - Tier B (native A2A) — stamp `AGENT_PORT`, plus any runtime-specific env.
4. Register with `services.AddSingleton<IAgentRuntimeLauncher, YourLauncher>()`
   in `ServiceCollectionExtensions`.
5. If Dapr Conversation is involved, honour `AgentLaunchContext.Provider` /
   `Model` and resolve `SPRING_LLM_COMPONENT` to `llm-{provider}` (the
   in-tree Dapr component naming convention from ADR-0038).
6. Add a `*LauncherTests` in `tests/Cvoya.Spring.AgentRuntimes.Tests/Launchers/`.

The dispatcher auto-discovers launchers via DI and routes by the runtime
entry's `launcher` id.

---

## 10. `concurrent_threads: true` author contract

> **Authoritative source:** [ADR-0041 — Actor-runtime contract for agent containers](../decisions/0041-actor-runtime-contract.md). This section is the author-facing summary; the ADR is the durable record. The same contract applies to Python SDK agents (see [Agent SDK § "concurrent_threads: true author contract"](agent-sdk.md#concurrent_threads-true-author-contract) for SDK-flavoured guidance).

The agent's `concurrent_threads` flag (sourced from the manifest's
`execution.concurrent_threads` slot) decides what runs concurrently inside the
agent's per-invocation container surface:

| Mode                      | Mailbox behaviour                          | In-container concurrency                 |
| ------------------------- | ------------------------------------------ | ---------------------------------------- |
| `concurrent_threads: false` (default-safe) | Per-agent serialization across all threads. | One runtime invocation in flight at a time. |
| `concurrent_threads: true` (opt-in)        | Per-thread channels dispatch independently. | N concurrent runtime invocations, one per active thread. |

The `false` mode is safe for any agent. The `true` mode is an explicit opt-in
that ships work to N concurrent runtime invocations inside the same container.
Session files don't fight (each turn gets `--resume <thread.id>`), but
**everything else in the container is shared and the author owns it**.

### The contract

An agent that sets `concurrent_threads: true`:

- **MAY** hold per-thread state in-process keyed by `thread.id`.
- **MUST NOT** bind fixed ports. Every per-thread port allocation must be
  ephemeral / dynamic — two concurrent turns binding `:8080` will collide.
- **MUST NOT** write outside `$SPRING_WORKSPACE_PATH/threads/<thread.id>/` for
  thread-local state. Files outside that subtree are visible to every
  concurrent turn and will race.
- **MUST NOT** assume any tool's child processes are uniquely theirs. No
  `pkill -f pytest` patterns — that pattern kills the test runner of every
  other concurrent turn in the same container.
- **MUST NOT** mutate shared global state — env vars, working directory,
  signal handlers. These propagate across every concurrent turn in the
  process.
- For CLI runtimes specifically: the system prompt MUST forbid the model from
  invoking long-running watchers (`pytest --watch`, `npm run dev`,
  `cargo watch`, `tail -f`, etc.). These never exit on their own and pin the
  container indefinitely under concurrency. The CLI launchers prepend a short
  guard fragment to the assembled system prompt automatically when this mode
  is on (see `LauncherPromptFragments.ConcurrentThreadsGuard`); the fragment
  is composed with — not a replacement for — the user's prompt.

Agents that cannot meet the contract stay on `concurrent_threads: false` and
accept head-of-line blocking on their mailbox. The trade-off is intentional;
see ADR-0041 § "Why HoL on `false` is acceptable for v0.1".

### Safe vs. unsafe patterns

**Safe — ephemeral port binding inside a per-thread workspace:**

```bash
# inside the runtime image, per turn
PORT=$(python -c 'import socket; s = socket.socket(); s.bind(("", 0)); print(s.getsockname()[1]); s.close()')
mkdir -p "$SPRING_WORKSPACE_PATH/threads/$SPRING_THREAD_ID/build"
cd "$SPRING_WORKSPACE_PATH/threads/$SPRING_THREAD_ID"
node server.js --port "$PORT" >build/server.log 2>&1 &
SERVER_PID=$!
trap "kill $SERVER_PID 2>/dev/null" EXIT
# … hit http://127.0.0.1:$PORT …
```

**Unsafe — fixed port + global cache + broad pkill:**

```bash
# DO NOT do this under concurrent_threads: true
node server.js --port 3000 &     # collides with every other turn
npm cache clean --force          # stomps a parallel turn's install
pkill -f node                    # kills every turn's server, not just yours
```

**Safe — thread-scoped temp dir:**

```bash
# isolate scratch state under the per-thread subtree the platform owns
SCRATCH="$SPRING_WORKSPACE_PATH/threads/$SPRING_THREAD_ID/.scratch"
mkdir -p "$SCRATCH"
# … work under $SCRATCH …
```

**Unsafe — shared global cache directories:**

```bash
# DO NOT do this under concurrent_threads: true
mkdir -p ~/.cache/myagent           # shared across every concurrent turn
echo "$RESULT" > /tmp/last-output   # likewise — /tmp is shared
export AGENT_STATE_FILE=/var/state  # mutates a global
```

### Surfacing

`spring agent validate <agent-id>` emits a `WARN` line whenever an agent's
persisted definition declares `execution.concurrent_threads: true`, with a
pointer to ADR-0041 and this section. The warning is not a blocker — opt-in
is allowed — but it is visible so authors do not flip the flag without seeing
the contract. An agent with no `concurrent_threads` slot in its definition
inherits the runtime's record default and the validator does not warn —
explicit opt-in is the trigger.
