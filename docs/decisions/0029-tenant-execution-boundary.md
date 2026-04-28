# 0029 — Tenant execution boundary and agent runtime execution contract

- **Status:** Accepted (2026-04-26) — v0.1 work. Defines the contract surface between tenant-scoped execution (agents and whatever composes them) and the Spring Voyage platform. Splits the boundary into two directional buckets: a small agent SDK contract (three lifecycle hooks) that the platform calls into tenant containers, and a minimal public API (one interface: A2A send) that tenant containers call into the platform. All message-shaped exchange across the boundary uses A2A 0.3.x as the wire protocol ([ADR 0027](0027-agent-image-conformance-contract.md)) with streaming responses where useful; container lifecycle and bootstrap stay out-of-band. Durable agent state is a per-agent persistent volume, not an interface. Capabilities such as durable timers, pub/sub for cross-agent observation, agent registry, and cloning live on the public Web API and reach agents via MCP — they do not grow the boundary. Declares what is **not** abstracted (orchestration substrate, actor host, LLM dispatch as a tenant-facing interface, state as a KV interface, tool discovery, observability) and why. Memory is deferred pending the thread-model design work ([#1123](https://github.com/cvoya-com/spring-voyage/issues/1123)). Does not ship new code — implementation flows through Area D in the v0.1 plan ([`docs/plan/v0.1/areas/d-adr-0029.md`](../plan/v0.1/areas/d-adr-0029.md)).
- **Date:** 2026-04-24
- **Related:** v0.1 milestone [#1](https://github.com/cvoya-com/spring-voyage/milestone/1), [ADR 0028](0028-tenant-scoped-runtime-topology.md) (the topology this boundary sits on; Decision C amended 2026-04-26 to make LLM access a public-API surface), [ADR 0027](0027-agent-image-conformance-contract.md) (every agent container is an A2A 0.3.x server — the precedent this ADR makes explicit as the boundary protocol), [ADR 0021](0021-spring-voyage-is-not-an-agent-runtime.md) (the principle this extends — the platform coordinates runtimes, doesn't implement loops), [ADR 0015](0015-dapr-as-infrastructure-runtime.md) (Dapr stays where it works), [ADR 0012](0012-spring-dispatcher-service-extraction.md) (dispatcher seam this builds on), [ADR 0018](0018-partitioned-mailbox.md) (being reframed by #1123 — this ADR does not re-decide mailbox shape), [PR #1177](https://github.com/cvoya-com/spring-voyage/pull/1177) (`ILlmDispatcher` — precedent for a platform-internal seam; see Consequences), [#1123](https://github.com/cvoya-com/spring-voyage/issues/1123) (conversation = participant-set; memory has two layers — blocks the memory contract).
- **Related code:** `src/Cvoya.Spring.Dapr/Execution/LlmDispatcher.cs`, `src/Cvoya.Spring.Dispatcher/LlmEndpoints.cs`, `agents/dapr-agent/agent.py`, `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs`.

## Context

[ADR 0028](0028-tenant-scoped-runtime-topology.md) put agent containers, workflow containers, and per-tenant Ollama on `spring-tenant-<id>` and kept `spring-placement`, `spring-scheduler`, and the daprd sidecars off the tenant network. The structural isolation is the whole point: tenant workloads cannot reach platform control-plane services, platform processes do not dual-home across tenant namespaces, and the dispatcher is the only cross-network bridge.

That topology broke the ephemeral dapr-agent container end-to-end. `agents/dapr-agent/agent.py` uses `dapr_agents.DurableAgent` wrapped in `AgentRunner.workflow(agent)`, which is a Dapr workflow and needs placement + scheduler. On `spring-tenant-default` those aren't reachable by design. The narrow unblock is to drop the workflow wrapper and run the agentic loop as plain Python in the container — no architectural change needed to ship it.

But the bug was a forcing function for a wider question. If tenant-scoped execution is a first-class architectural concern (which is what justifies the topology cost of 0028), then the platform's coupling to any specific orchestration substrate is a liability at exactly the boundary where tenancy matters most. If tenant-authored code has to be Dapr-Workflow-shaped, every choice the orchestration substrate makes leaks into tenant code.

We explored several abstractions and collapsed most of them:

- `IWorkflowEngine` with Dapr / LangGraph / Temporal implementations. Rejected — their execution models (imperative durable-task vs graph-with-state vs saga) are incompatible enough that any shared interface either shapes to one (leaking into the others) or is a least-common-denominator none uses well.
- `IAgentOrchestration` as a platform primitive. Rejected — an orchestrator's structural contract (receive message, invoke agents, return result) is identical to an agent's. Orchestrators are agents whose dominant behaviour is delegation. No new hosting concept or interface is required; orchestration is a pattern implementers compose, not a platform primitive.
- `IActorHost` / platform-abstracted actors. Rejected — [ADR 0015](0015-dapr-as-infrastructure-runtime.md) and the worker's role in [ADR 0028](0028-tenant-scoped-runtime-topology.md) keep actors as platform infrastructure. They represent the platform's internal model of tenants, units, and agents, not tenant-facing machinery.

What remained after the collapse is a small set of contracts at a single structural boundary: the line between tenant-scoped execution and the platform. This ADR names that boundary and specifies what crosses it.

One constraint shaping the decisions below: the SV interaction model has no "task" or "job" primitive at this boundary. [#1123](https://github.com/cvoya-com/spring-voyage/issues/1123) reframes *conversation* as the participant-set relationship and leaves "task" as a user-level work-categorization concept (referenced by `#name` inside messages), not a runtime execution unit. An agent does not "receive a task" — it receives messages, possibly on many conversations, and decides what to do. Scoping anything at this boundary to a "task lifetime" would hard-code a concept the model does not have.

## Decision

**The platform/tenant interface boundary has two directional buckets, one bootstrap payload, and a per-agent persistent volume.** Tenant code implements three lifecycle hooks; the platform exposes one public API; an immutable context object hands the two together at startup and carries the endpoints and credentials for everything else tenant code needs; durable agent state lives on a filesystem volume that the platform provisions and mounts.

### Bucket 1 — Agent SDK contract (platform → tenant)

One contract. The platform calls into the tenant container; the tenant container implements these hooks:

| Hook | Purpose |
|---|---|
| `initialize(context)` | Platform delivers the `IAgentContext` bootstrap payload. Container wires up handles, loads identity, opens telemetry. The agent inspects the workspace volume to decide whether it is starting fresh or resuming after a crash — platform does not signal this; workspace contents do. |
| `on_message(message)` | The SDK-level handler the agent's A2A server invokes for each inbound A2A 0.3.x request (carrying conversation metadata). Streaming responses supported per the A2A spec. Agent dispatches however it chooses — agentic loop, static graph, single-shot completion, AI-driven routing. |
| `on_shutdown(reason)` | Platform signals graceful termination (including cancel). Container flushes any in-memory state to the workspace volume, closes telemetry. |

This is the surface Spring Voyage publishes as its agent SDK (one SDK per supported language — Python, C#, others added as needed). It is **not** a public API; it's an embeddable contract. Anything outside these three hooks is out of scope for the SDK. In particular, the SDK does not dictate whether the container runs an agentic loop, a static orchestration graph, a single LLM call, or an AI-driven router — that is an implementation choice.

### Bucket 2 — Public platform API (tenant → platform)

One interface. The platform implements it and publishes it on the public API surface. Tenant code calls it.

| Interface | Purpose |
|---|---|
| A2A send | An A2A 0.3.x request from the agent to the platform's routing endpoint. Platform routes to the target peer — in-network where reachable, via the dispatcher proxy where they aren't. Standard A2A response semantics including streaming where useful. |

This is the platform's **public API surface** for tenant-scoped code, with the properties that implies:

- **Language-agnostic transport** (HTTP / gRPC on the wire). Per-language SDKs wrap the wire protocol for ergonomics, but the wire is the contract.
- **Semver versioning** with deprecation cycles. Every consumer outside platform code — agent containers, tenant tooling, in-cloud integrations — sees the same contract.
- **Uniform authz**: tenant identity + scoped tokens gate every call.
- **Testable in isolation**: a test harness that implements the interface lets any agent run locally against a fake platform.
- **Reusable beyond agents**: an operator CLI or a tenant's custom tooling can call the same API an agent would, modulo auth. That's a property, not a leak.

### The bootstrap — `IAgentContext`

Neither an API call nor a hook — a **read-only bundle of data and handles delivered at init**. It carries:

- Static metadata: tenant / unit / agent identity, tenant-level configuration.
- Endpoint handles for Bucket 2: A2A send endpoint, scoped credentials.
- Endpoint handles for platform-provided *services* that are not Bucket-2 interfaces: LLM provider endpoints (platform-hosted Ollama, managed-provider proxy endpoints) with scoped credentials; MCP endpoint(s) for tool discovery; telemetry collector endpoint.
- **Workspace mount path** — the filesystem location where the agent's persistent volume is mounted inside the container.

`IAgentContext` is the handshake that wires Bucket 1 to Bucket 2 and also catalogues every platform-provided endpoint and on-disk location the agent can reach. Adding a new piece of init-time data usually means extending this context — not adding a new interface.

### Durable state: a per-agent persistent volume

Each agent gets a **persistent filesystem volume** mounted into every container instance of that agent. The agent writes whatever it wants — worktrees, SQLite databases, checkpoint files, git repositories, tool caches, partial results — in whatever shape its implementation prefers. The platform is opaque to the contents.

This mirrors how the external agent runtimes SV coordinates ([ADR 0021](0021-spring-voyage-is-not-an-agent-runtime.md)) actually work — Claude Code, Codex, Aider and similar tools already use the filesystem as their durable state. Giving them a volume *is* giving them state; no KV interface, no serialization shape, no client library, no SDK-defined surface to maintain.

**Lifetime.** Volume lifetime is bound to the agent's lifetime — not to any container instance:

- **Persistent agents:** volume survives container restarts; reclaimed when the agent is deleted.
- **Ephemeral agents:** volume survives mid-execution crashes, so a restarted instance sees whatever the previous instance checkpointed. Reclaimed together with the container when the agent declares the work done.

**Recovery is agent-owned.** On startup, the agent inspects the workspace and decides whether to resume from a checkpoint or start fresh. The platform does not pass a `state` parameter and does not have a separate `on_recover` hook — both would hard-code a recovery model. The agent chooses what to persist, when, and how to interpret it on restart, matching the pattern established by the tools SV coordinates.

**Scope is per-agent and private.** Cross-agent data transfer stays in A2A payloads. No shared-storage semantics, no inter-agent filesystem primitives.

**Platform concerns are volume-level.** Quotas, encryption at rest, backup / DR, snapshot, migration — all handled at the volume layer by the deployment (Podman named volume in OSS, PVC in K8s cloud) without crossing the tenant boundary. Observability stays at the volume metric layer (size, growth rate, last-write) — opaque to content.

### LLM access: native APIs, not a platform interface

Tenant containers talk to LLM services **directly, using the provider's native API** (Ollama's REST, OpenAI chat completions, Anthropic messages, etc.). The platform:

- Hosts the LLM services the tenant can use (platform-wide Ollama; proxies for managed providers where the platform is the trust boundary for the API key).
- Delivers the endpoint URL and a scoped credential through `IAgentContext`.
- Authorises each call at its own endpoint (token check), meters, logs.

This means the provider's public API surface *is* the contract. Ollama's API becomes part of SV's public API by hosting, not by wrapping. No `ILlmDispatcher`-style wrapper at the tenant boundary, no SV-specific provider abstraction in tenant code, no provider-switching semantics the platform has to maintain across languages.

`ILlmDispatcher` ([PR #1177](https://github.com/cvoya-com/spring-voyage/pull/1177)) stays as a **platform-internal seam** for worker-side code that makes LLM calls on behalf of hosted agents or for internal uses (routing decisions, Tier 1 screening per [ADR 0020](0020-tiered-cognition-for-initiative.md), classification). It is not part of the boundary defined by this ADR.

### Wire protocol: A2A 0.3.x

All message-shaped exchange across the boundary uses A2A 0.3.x as the wire protocol. This makes explicit what [ADR 0027](0027-agent-image-conformance-contract.md) already implies — every agent container is an A2A 0.3.x server on `:8999`, and the platform consumes that endpoint. There is no separate "platform-to-agent" RPC; there is just A2A.

This means:

- **Bucket 1's `on_message`** is the SDK-level hook the agent's A2A server invokes for each inbound A2A request. The SDK wraps the A2A server runtime; agent authors implement the hook, not the A2A protocol details directly.
- **Bucket 2's "A2A send"** is the agent issuing an A2A 0.3.x request to the platform's routing endpoint. Same protocol, opposite direction.
- **Skills** (tool-shaped exchange) ride on MCP per [ADR 0021](0021-spring-voyage-is-not-an-agent-runtime.md), separately from A2A.

**Streaming.** A2A 0.3.x supports streaming responses (server-sent events) for long-running interactions. The SDK exposes streaming where the spec supports it. The platform pattern is one A2A request per inbound message; the agent's response may be streamed (partial results, status updates) per the A2A spec.

Long-lived bidirectional push channels (websocket-style — see e.g. OpenAI's WebSocket mode) are where the industry is heading. We do not commit to that pattern in this ADR — when A2A's spec catches up or a forcing case appears, we revisit. The boundary is bound to A2A's evolution; we accept that coupling because A2A is the most credible cross-vendor agent protocol and the platform is already conformant.

**What is explicitly out-of-band of A2A:**

- **Container lifecycle** (start, stop, restart, crash detection) is platform-owned via container-runtime primitives (Podman in OSS, K8s in cloud). The platform actor uses those primitives; A2A is for messages, not for "your container is starting."
- **Bootstrap** (`IAgentContext` delivery) happens at container start, before any A2A traffic, via env vars and mounted files. The agent is fully configured by the time it begins accepting A2A.
- **`on_shutdown`** is signalled by container-runtime SIGTERM with a graceful-shutdown window. The SDK catches the signal and invokes the hook. A reserved A2A "shutdown" message type may emerge later as a complement, but is not required by this ADR.

### Memory is deferred

Memory is *not* specified by this ADR. [#1123](https://github.com/cvoya-com/spring-voyage/issues/1123) reframes conversations as participant-set relationships and establishes that memory has two layers (per-conversation and agent-level spanning conversations), with cross-conversation flow governed by tenant/unit/agent policy. The memory contract has to land inside that model; specifying it here would either pre-empt the conversation-design work or ship a shape that will need rewriting when that work lands.

Interim state: agents use the workspace volume for private bookkeeping the agent itself owns. Anything resembling "what did we talk about last time" or "what have I learned across everyone I've worked with" waits for the follow-up memory ADR.

### Failure recovery splits the same way as the boundary

- **Process and container lifecycle** is platform-owned. A Dapr actor on `spring-net` supervises each tenant container: start, crash detection, restart, reclaim-on-done. Tenant containers implement the three lifecycle hooks; the actor drives them. The workspace volume persists across restarts so a restarted instance can resume.
- **Recovery** is agent-owned. The agent decides what to checkpoint to the volume and how to interpret on-disk state at startup. The platform provides the durable filesystem and the restart signal; the agent picks its own semantics.

## What is explicitly NOT abstracted

Every interface is load-bearing forever. The ones below were considered and rejected or deferred; documenting them here prevents rediscovery under pressure later.

- **Workflow engine / orchestration substrate.** Orchestrators are agents. The implementation chooses Dapr Workflows (inside the container, with all the caveats 0028 implies), LangGraph, Temporal, AI-driven routing, or plain imperative code. The platform is not opinionated.
- **Actor host.** Actors are platform infrastructure representing tenants, units, and agents internally. They are not tenant-facing and do not cross the boundary this ADR defines.
- **LLM access as a tenant-facing interface.** Provider APIs are the contract. Endpoints + credentials are delivered via `IAgentContext`. Rationale above.
- **Durable state as a structured interface.** Rejected in favour of a per-agent filesystem volume. A KV / object-store shape would duplicate what agents already do on disk and would force SV to maintain an SDK, serialization conventions, query patterns, and size semantics that add no value over the filesystem primitive every external agent runtime already targets.
- **Tool discovery.** MCP is already a cross-tool protocol ([ADR 0021](0021-spring-voyage-is-not-an-agent-runtime.md)). The platform delivers MCP endpoint URLs via `IAgentContext`; the agent speaks MCP. No SV-specific interface.
- **Observability.** Standardise on OpenTelemetry. The collector endpoint is in `IAgentContext`. No new interface.
- **Secrets provision.** Agent-scoped credentials are delivered in `IAgentContext`. If an agent needs dynamic secret resolution beyond that, it uses whatever platform secret primitive is exposed (Dapr secrets today); that is not promoted to a tenant-facing interface.
- **Memory.** Deferred pending [#1123](https://github.com/cvoya-com/spring-voyage/issues/1123). Not in scope for this ADR; interim use of the workspace volume covers private bookkeeping only.

### Capabilities reached through MCP, not at the boundary

Several capabilities that an earlier draft proposed deferring belong on the **public Web API**, not at the SDK boundary. Agents reach them through the platform's MCP server (which is itself implemented over the public Web API per [ADR 0021](0021-spring-voyage-is-not-an-agent-runtime.md)) and through skills / commands the agent author composes on top. This keeps the boundary minimal — three hooks plus A2A send — while the platform's actual surface area stays comprehensive.

Capabilities that follow this pattern:

- **Durable timers / scheduled waits.** "Wake me at T," "remind agent X every N minutes," "fire when condition Y holds." Public Web API verbs; surfaced as MCP tools the agent invokes.
- **Pub/sub for cross-agent observation.** "Tell me when agent Y finishes," "stream the steps agent Z performs as they happen," "subscribe to events emitted by unit U." These are subscriptions over the public Web API, surfaced as MCP tools. External events the platform wants to push to an agent still arrive as A2A inbound messages — pub/sub gives the *agent* an explicit subscription verb without inventing a tenant-facing pub/sub bus.
- **Agent registry / dynamic discovery.** "Find me agents that can do X," "what agents are in unit U," "what's the status of agent Y." Public Web API queries; MCP tools.
- **Cloning.** The mechanism by which an agent can replicate itself or a unit can request a child be cloned. Public Web API; MCP tool.

Future capabilities of the same shape (anything message-shape-agnostic that the platform exposes uniformly to humans, tools, and agents) follow the same pattern: define on the public Web API, expose via MCP, optionally wrap as agent-side skills.

The boundary contract does not grow when a new such capability is added. Only the public Web API and the platform's MCP server grow.

## Resolved during review (formerly open)

- **Lifecycle control channel.** Resolved: there is no separate channel. **Container lifecycle** is managed by container-runtime APIs (Podman in OSS, K8s in cloud) — that is the platform actor's mechanism. **Agent control messages** ("stop what you are doing," "report status," "drain") are not distinguishable from domain messages at the protocol level — they are A2A messages the agent interprets according to its own conventions. Should a future need ever appear to control the *agent runtime* (e.g. the `claude` CLI process inside a container) independently of the container instance — restart-tool-without-restart-container — an SV sidecar would be added at that point. No forcing case today; not in scope.
- **Ephemeral-agent provisioning on demand.** Resolved: agents do not know about containers and have no way to ask for one. To delegate work, an agent sends an A2A message to the recipient agent. The platform decides whether the recipient's configuration calls for a fresh ephemeral container instance or routing to a persistent one. The provisioning decision is the platform's, never the calling agent's.

## Open questions

- **Agent-runtime CLI-style commands.** Tools like Claude Code, Codex, etc. expose `/`-prefixed commands (`/help`, `/clear`, `/resume`) that a human user would type into the running tool. If we want to drive these programmatically against an already-running agent — as opposed to terminating the container and starting fresh with different configuration — there is currently no specified channel. Open question whether to model these as A2A messages (with reserved semantics the SDK adapts into the underlying tool's CLI), as a separate runtime control channel (the SV sidecar shape mentioned above), or out of scope entirely. Needs more thinking; not in scope for V2.1 but flagged so the decision lands deliberately when a forcing case appears.
- **Cloning policies.** The mechanism (a public-Web-API verb, surfaced via MCP) is clear from the section above. The *policy* — when an agent is allowed to clone itself, when a unit can clone a child, who pays, what limits apply — has not been figured out. Out of scope for this ADR.

## Alternatives considered

- **Ship the narrow dapr-agent fix and defer the boundary.** Gets ephemeral dispatch working today but leaves the next category-class problem (persistent agents, tenant-authored workflow, LangGraph implementers) to re-decide under pressure with sunk-cost bias toward whatever was shipped first. This ADR does not block the narrow fix — Stage 0 ships it — but records the boundary the broader work lines up against.
- **`IWorkflowEngine` with multiple implementations.** Would make Dapr Workflows, LangGraph, and Temporal interchangeable behind one interface. Rejected — their execution models are incompatible enough that any shared interface either shapes to one (leaking into the others) or is a least-common-denominator none uses well. Letting orchestrators live inside agent containers and pick their own substrate is the simpler answer.
- **`IAgentOrchestration` as a distinct platform concept.** Would name the "run an orchestration" operation separately from agent dispatch. Rejected — the structural contract is identical to an agent's. Collapsing orchestrators into agents deletes an entire hosting concept with no loss of expressiveness.
- **`ILlmDispatcher` (or equivalent) as a tenant-facing interface.** Would wrap provider APIs behind an SV-specific abstraction that every tenant container calls. Rejected — the provider APIs are already the industry's cross-provider contract. Wrapping them means SV maintains provider-switching semantics across every supported language, absorbs provider-API drift into its own version surface, and gains nothing the hosting + scoped-credential approach does not already give us. The dispatcher stays as a worker-internal seam, not a tenant boundary.
- **Keep `DaprChatClient` as the LLM path for Python agents.** Would give Python agents Dapr Conversation's provider abstraction. Rejected for the same reason as the line above, and because Dapr Conversation requires a sidecar the tenant container does not have.
- **`IAgentState` as a KV / structured-state interface.** Would expose get/put with typed serialization, transactions, and per-write authz at the platform boundary. Rejected — the tools SV coordinates already use the filesystem for state; a KV interface forces a translation layer that wins nothing in exchange for a forever SDK surface, serialization convention, and size-semantics commitment. A volume is simpler, matches the tools, and pushes the "shape" decision into agent code where it belongs. The tradeoff: the platform cannot inspect structured state for debug, cannot encrypt-per-write with per-tenant keys, and cannot migrate across state-store backends transparently. Those become volume-level operations (snapshot inspect, storage-driver encryption, volume migration). Different operational patterns, not worse ones.
- **Continue with `DurableAgent` + dual-attach placement/scheduler to the tenant network.** Keeps the ephemeral agent code unchanged. Rejected — reverses [ADR 0028](0028-tenant-scoped-runtime-topology.md)'s isolation guarantee; every tenant container gains reach into platform actor coordination.
- **Per-agent Conversation-only sidecar in the tenant network.** Gives the agent Dapr Conversation without placement/scheduler. Rejected — adds a sidecar per container without solving the `DurableAgent.run` workflow dependency; the ephemeral loop still has to be rewritten.

## Consequences

### What this buys

- **Orchestration freedom for implementers.** A tenant can ship a LangGraph implementation, an AI-driven router, or a deterministic state machine — all as agents that speak the same boundary. The platform is not in the way.
- **LLM freedom for implementers.** Agent authors use whatever client library they already know for their provider. No SV-specific wrapper to learn, no wrapper version to chase.
- **State freedom for implementers.** Worktrees, SQLite, git, flat files, whatever shape the agent wants. No SV-defined data surface.
- **Versionable, testable, reusable public API.** Bucket 2 is semver, HTTP-transported, auth-gated, and mockable. A test harness that implements the one interface (plus a tmpdir mounted as "workspace") lets agents run without the platform.
- **Multi-language SDK story.** Bucket 1 is three hooks; a new-language SDK is a small, bounded task.
- **Small abstraction surface.** One interface, three hooks, one bootstrap object, one volume. Any addition has to justify why it cannot live inside `IAgentContext`, the lifecycle contract, the volume, or the public Web API exposed via MCP. Interfaces are forever.
- **Dapr stays where it fits.** Storage, pub/sub (platform-internal), platform workflows, actor-based supervision all continue to use Dapr. The boundary is about what tenant code sees, not about ripping out the internal substrate.

### Deliverable shape

The work this ADR opens lands as two artifacts that outlive any specific implementation choice:

- **A well-defined, well-documented public Web API.** Versioned, auth-gated, language-agnostic. Used by the agent boundary (A2A send), by the platform's MCP server (timers, pub/sub, registry, cloning, future capabilities), by external integrators, and by operator tooling. The same surface for everyone.
- **Requirements and extensibility points for agent runtimes.** A specification for what makes an agent container conformant: A2A 0.3.x server, MCP client, three lifecycle hooks, native-API LLM consumption, workspace-volume convention. Extends [ADR 0027](0027-agent-image-conformance-contract.md). Adding a new agent runtime is a thin adapter against this spec, not platform changes.

### What this costs

- **A wire protocol and an SDK to maintain.** Parts of both exist today (the A2A transport is live; agent containers already consume env-var-delivered context); V2.1 formalises them. The cost is the write-it-down-and-version-it work, not new infrastructure.
- **Credential-in-context as the trust model.** The platform delivers scoped credentials to the container at init. Any agent code (or anything the container pulls in) sees those credentials. This is a known pattern but worth naming as a property of the boundary; it constrains how we'd eventually want encrypted-at-rest tenant state to work (see [#1170](https://github.com/cvoya-com/spring-voyage/issues/1170)).
- **Volume-level rather than data-level platform operations.** Encryption at rest, debug inspection, backup / DR, and migration all happen at the volume layer rather than through a structured state API. Standard patterns, but a different operational posture than a KV interface would give.
- **Control-channel complexity for lifecycle supervision.** Platform actors driving tenant-container lifecycle hooks over a cross-network channel is new plumbing; see the open-questions section.
- **Discipline required to hold the line.** The interface ceiling is small on purpose. Follow-up work has to resist adding interfaces without a forcing use case.

### Known limitation: topology metadata visibility

The boundary contract this ADR defines, combined with [ADR 0028](0028-tenant-scoped-runtime-topology.md)'s topology, gives V2.1 a content-layer isolation story that is necessary but not sufficient for the most stringent compliance postures. The platform inherently sees structural and operational metadata: which units exist per tenant, which agents are in each, configurations, manifests, message routing topology, message rates, container sizes. Routing, supervision, and observability all require it; encrypting it would defeat the operational signals the platform needs.

Tenants whose threat model treats topology and operational metadata as proprietary IP — not just message contents — are not fully addressed by V2.1's architecture. The pragmatic long-term answer is a **tier model**: default tier shares infrastructure with content-layer encryption + contracts + audit (what V2.1 delivers); a premium tier offers dedicated deployment so the topology lives only on the tenant's own platform stack. Industry SaaS converges on this shape.

Tracked separately as a V3+ architectural decision in [#1203](https://github.com/cvoya-com/spring-voyage/issues/1203). The terminal-architecture work in [#1170](https://github.com/cvoya-com/spring-voyage/issues/1170) — encrypted-at-rest tenant data with KMS integration — is part of the content-layer story and complementary to, not a substitute for, the tier-model decision.

### Revisit ADR 0028 Decision C (per-tenant Ollama)

[ADR 0028](0028-tenant-scoped-runtime-topology.md) made LLM services per-tenant (Decision C) because the tenancy boundary was enforced at the network layer — putting Ollama on the tenant network was the mechanism for "tenant A can't reach tenant B's Ollama." Moving LLM access to **native APIs + scoped credentials delivered via `IAgentContext`** makes tenancy an API-layer property: a platform-wide Ollama with tenant-scoped auth keys is functionally equivalent, and the single-instance deployment avoids the per-tenant provisioning / idle / model-cache costs that [#1164](https://github.com/cvoya-com/spring-voyage/issues/1164) was filed to address.

This ADR does not amend 0028 directly; it flags the implication and recommends a follow-up amendment ADR (or superseding decision) once this ADR is accepted. Until that amendment lands, 0028 Decision C remains the deployed topology.

### Memory and the conversation model

Because memory is deferred, there is an implicit dependency on [#1123](https://github.com/cvoya-com/spring-voyage/issues/1123) landing before the memory contract can be specified. That contract will need to express the two-layer model ("what we've done together" per conversation + "what I've learned across everything" agent-level) and the policy-controlled flow between them. It will likely also interact with SV's mapping of conversations to external "session/chat/thread" concepts in consuming tools. This ADR intentionally leaves that surface open.

### Staging (see V2.1 tracker for canonical schedule)

- **Stage 0 (immediate, not V2.1 — unblocks the ephemeral dapr-agent today).** Drop the Dapr-Workflow execution wrapper from `agents/dapr-agent/agent.py`. Keep the agentic loop as plain Python (reimplemented, borrowed from `dapr_agents`' lower-level classes, or substituted from another library). An ephemeral agent is defined by container-lifetime-tied-to-completion-of-work, not by any specific loop implementation.
- **Stage 1 (V2.1).** This ADR. Contract surface specifications for Buckets 1 and 2, `IAgentContext`, and the per-agent volume. No code changes.
- **Stage 2 (V2.1).** A2A named seam. Existing duplicated caller-side paths (direct on tenant network vs dispatcher proxy) collapse behind one transport abstraction.
- **Stage 3 (V2.1).** Tenant boundary implementation — `IAgentContext` (with LLM endpoints + scoped credentials, MCP endpoints, telemetry collector, workspace mount path), lifecycle contract (three hooks), per-agent workspace volume provisioning, platform actor supervision over the now-resolved control channel. Retire `DaprChatClient` usage in tenant containers. Separately file an ADR 0028 amendment for platform-wide Ollama.
- **Stage 4 (after V2.1 memory design settles).** Memory contract. Lands inside the framework [#1123](https://github.com/cvoya-com/spring-voyage/issues/1123) establishes, not ahead of it.

Stages 2–3 each get their own tracker issue under [V2.1 (#2)](https://github.com/cvoya-com/spring-voyage/milestone/2); Stage 0 gets a standalone issue outside V2.1 so it can ship ahead of the ADR being accepted.
