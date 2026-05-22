# Glossary

Definitions of key terms used throughout the Spring Voyage documentation.

**Spring Voyage** is an open-source collaboration platform for teams of AI agents — and the humans they work with. Throughout this glossary, "platform" refers to Spring Voyage. The platform delivers messages between agents; it does not orchestrate. Collaboration between humans and agents is the larger frame the platform exists to make tractable.

---

**A2A (Agent-to-Agent)**
An open protocol for cross-framework agent communication. The platform drives every agent-runtime container over A2A, and Spring agents can collaborate with agents built on other frameworks (Google ADK, LangGraph, etc.).

**Activation**
What causes an agent to wake up and act. Activation triggers include a direct message, a pub/sub subscription, a Dapr reminder or timer, or the initiative cognition loop.

**Address**
A routable identity for any addressable entity. Shape: `(Scheme, Guid)` — a scheme (`agent`, `unit`, `human`, or `connector`) plus the addressed actor's stable `Guid`. Canonical wire form: `scheme:<32-hex-no-dash>` (e.g. `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7`). Parsers are lenient (the dashed Guid form is accepted everywhere); the emit form is uniform. There is no path-shaped address, no `@<uuid>` form, and no namespace+name pair — identity is the `Guid`. An address identifies an actor; it does not encode hierarchy. See [Data & identity](architecture/data-and-identity.md) and [ADR-0036](decisions/0036-single-identity-model.md).

**Agent**
An autonomous AI-powered participant — the fundamental building block of the platform. Every agent has an identity, a mailbox, and an execution config, and reasons about how to respond to the messages it receives. A **unit** is an agent that has children; a **leaf agent** is an agent with none.

**AgentActor**
The Dapr virtual actor implementing a leaf agent. Owns the per-thread mailbox channels, the observation channel, lifecycle status, and initiative state.

**AgentRuntime**
The in-container execution engine that runs an agent's turn — Claude Code, Codex, Gemini CLI, or the Spring Voyage agent. Declared as data in `runtime-catalog.yaml`. The user-facing execution config is the tuple `(runtime, model)` — a runtime plus a structured `model: {provider, id}`. See [Agent runtime](architecture/agent-runtime.md) and [ADR-0038](decisions/0038-agent-runtime-and-model-provider-split.md).

**AgentMemory**
An agent's single, ordered, append-only memory store. Entries are **memory entry** records with optional `thread_id` and `threadOnly` attributes; per-thread visibility is governed by the thread's **ThreadMemoryPolicy**. Memory is read and written through the `sv.memory.*` MCP tools. See [Messaging § Agent memory](architecture/messaging.md#agent-memory).

**Boundary**
The interface a unit exposes when acting as a member of a parent unit. Controls what is visible (transparent, translucent, or opaque) and what operations are projected, filtered, or synthesized.

**Clone**
A platform-managed copy of an agent, spawned to handle concurrent work. Governed by the agent's cloning policy (`none`, `ephemeral-no-memory`, `ephemeral-with-memory`, `persistent`) and an attachment mode (`detached`, `attached`). Units cannot be cloned — composition is a unit's scaling mechanism.

**Cognition loop**
The five-step reasoning process agents use during initiative: perceive, reflect, decide, act, learn.

**Collaboration**
The active shared space where participants converse, coordinate, and get work done — the UX active-workspace surface. Recorded by the system as a **thread** and presented in product navigation as an **engagement**. See [Threads, engagements, and collaborations](concepts/threads.md).

**Connector**
A pluggable bridge between an external system (GitHub, Slack, arXiv, …) and a unit. Provides inbound event translation (external events become one-way platform messages) and optional outbound skills. A connector is a non-routable bridge — not an actor; nothing routes a message *to* it ([ADR-0053 § 5](decisions/0053-units-are-agents-and-one-way-delivery.md)).

**Control channel**
A partition of the agent's mailbox for platform control messages (`Cancel`, `StatusQuery`, `HealthCheck`, `PolicyUpdate`). Control messages are never blocked behind work.

**Dapr**
A distributed application runtime providing building blocks (actors, pub/sub, state, secrets, workflows) as a sidecar process. The infrastructure foundation of Spring Voyage.

**Directory**
A registry of agent expertise, queryable within and across units. A unit's effective expertise is the recursive union of its own declared domains and every descendant's. A runtime reads it through the `sv.directory.*` and `sv.expertise.*` MCP tools.

**display_name**
The human-facing label for an actor (unit, agent, human, connector, tenant), used in wizard listings, activity-log narrative, and CLI table output. Not unique, not addressable, not a foreign-key target. The platform rejects any `display_name` that parses as a `Guid`, so a Guid-shaped token is unambiguously identity. See [Data & identity](architecture/data-and-identity.md) and [ADR-0036](decisions/0036-single-identity-model.md).

**Domain message**
A `Message` of `MessageType.Domain` — a one-way **event** ("something happened") delivered to a unit or agent. The platform never inspects the payload. The sender is not blocked on a return value; a dispatch response is recorded on the thread, never routed back. See [Messaging](architecture/messaging.md) and [ADR-0053](decisions/0053-units-are-agents-and-one-way-delivery.md).

**Engagement**
The product / UX narrative term for the enduring relationship between participants over time. Recorded by the system as one or more **threads** and worked in as a **collaboration**. See [Threads, engagements, and collaborations](concepts/threads.md).

**Execution config**
How the platform runs an agent or unit: `(runtime, model, image, hosting)`. Fields left empty inherit from the parent unit, or from tenant defaults for a top-level entity.

**Expertise profile**
A structured description of what an agent knows and how well it knows it (domains with a level: `beginner` / `intermediate` / `advanced` / `expert`). Seeded from configuration, optionally evolved through observation and learning.

**HumanActor**
The Dapr virtual actor representing a human participant. Routes notifications and enforces permission levels.

**Initiative**
An agent's capacity to act without an external trigger. Four levels — `Passive`, `Attentive`, `Proactive`, `Autonomous` — each granting a wider self-modification scope. Governed by unit-level policies. See [Initiative](concepts/initiative.md).

**Mailbox**
An agent's inbound message system, logically partitioned into a control channel, per-thread FIFO channels, and an observation channel.

**Memory entry**
A single record in an agent's **AgentMemory**. Shape: `{ id, timestamp, payload, thread_id?, threadOnly? }`. The `payload` may be any kind of memory artifact (fact, lesson, observation, reasoning step, …); the platform stores them uniformly. The `threadOnly` attribute is stamped at write time from the thread's **ThreadMemoryPolicy** and controls cross-thread visibility. **Tasks are memory entries** — there is no typed `task` platform concept.

**Message**
A typed communication between addressable entities. Fields: `Id`, `From`, `To`, `Type` (`MessageType`), `ThreadId`, `Payload`, `Timestamp`.

**MessageType**
Separates domain traffic from control traffic. `Domain` carries work the runtime interprets; `Cancel`, `StatusQuery`, `HealthCheck`, and `PolicyUpdate` are control types the platform handles directly.

**Model**
A specific LLM identified by the structured pair `{provider, id}` (e.g. `{provider: anthropic, id: claude-opus-4-7}`). The provider is intrinsic to the model. See [ADR-0038](decisions/0038-agent-runtime-and-model-provider-split.md).

**ModelProvider**
The company or service whose API hosts a set of LLMs — `anthropic`, `openai`, `google`, `ollama`, and future additions. Declared as data in `runtime-catalog.yaml`. The credential boundary the platform resolves against. See [ADR-0038](decisions/0038-agent-runtime-and-model-provider-split.md).

**Observation channel**
A partition of the agent's mailbox for events from pub/sub subscriptions, reminders, and timers. Processed in batch by the initiative cognition loop.

**OssTenantIds.Default**
The deterministic v5 UUID owning every tenant-scoped row in a fresh OSS install: `dd55c4ea-8d72-5e43-a9df-88d07af02b69`. Computed over a fixed namespace and the label `cvoya/tenant/oss-default`, pinned as a literal in `src/Cvoya.Spring.Core/Tenancy/OssTenantIds.cs`. See [Data & identity § The OSS default tenant](architecture/data-and-identity.md#the-oss-default-tenant).

**OssTenantUserIds.Operator**
The deterministic v5 UUID owning the single OSS-operator `TenantUser` row: `5c4c8e29-d91b-5b50-8651-64536cfb68ee`. Computed over a fixed namespace and the label `cvoya/tenant-user/oss-operator`, pinned in `src/Cvoya.Spring.Core/Tenancy/OssTenantUserIds.cs`. In OSS every `Human` resolves to this `TenantUser`. See [Data & identity](architecture/data-and-identity.md#the-oss-default-tenant) and [ADR-0047](decisions/0047-platform-user-human-split.md).

**Package**
An installable bundle of domain-specific content: agent and unit definitions, skills, and templates. How the platform stays domain-agnostic while supporting specific domains. See [Packages](concepts/packages.md) and [ADR-0035](decisions/0035-package-as-bundling-unit.md).

**Platform MCP tools**
The platform-provided MCP tools an agent runtime consumes, all named `sv.<area>.<verb>` — the areas are `directory`, `memory`, `messaging`, `runtime`, and `expertise`. The message-delivery surface is exactly two tools — `sv.messaging.send` and `sv.messaging.multicast` — on a delivery-acknowledgement contract; the platform delivers messages, it does not orchestrate. There are no `delegate_to` / `fanout_to` tools — "delegation" is message content the recipient's runtime interprets. The tools are exposed through **one** worker-side MCP server, authenticated by **one per-turn MCP session token**. See [Messaging § The platform MCP tool surface](architecture/messaging.md#the-platform-mcp-tool-surface) and [ADR-0054](decisions/0054-one-mcp-server-one-execution-host.md).

**Routing decision**
An optional, explicit record of how a unit's runtime routed work — published as a `DecisionMade` activity event when a runtime calls `sv.runtime.report_decision`. A plain `sv.messaging.*` delivery publishes a `MessageSent` activity instead; recording the decision itself is never required.

**Skill**
A bundle of a markdown prompt fragment plus optional tool definitions. The smallest unit of reusable domain knowledge. A package *ships* a skill; an operator *equips* it on a unit or agent. See [Skills](concepts/skills.md).

**Tenant**
An isolated organizational unit — the top-level boundary for access control, billing, and resource isolation. Modelled in the OSS core as a `tenant_id` value on every tenant-scoped entity, not as infrastructure.

**TenantUser**
The authenticated principal of Spring Voyage scoped to one tenant — the operator in OSS, tenant members in cloud. Distinct from `Human` (a configuration entity declared by a package). Display-side connector identity — GitHub login, Slack handle — is owned by the `TenantUser`, stored on `TenantUserConnectorIdentity` rows. See [Tenants § TenantUser](concepts/tenants.md#tenantuser-the-authenticated-principal) and [ADR-0047](decisions/0047-platform-user-human-split.md).

**Thread**
The unique, persistent, system-level record for a set of two or more participants, containing their lifelong shared exchanges and activity. The participant set IS the identity: there is exactly one thread per unique participant set; adding or removing a participant produces a different thread. This is the system / architectural concept used in code, schema, and APIs; the product presents a thread as an **engagement** and the user works inside it as a **collaboration**. See [Threads, engagements, and collaborations](concepts/threads.md) and [ADR-0030](decisions/0030-thread-model.md).

**ThreadMemoryPolicy**
Per-thread policy that sets the default `threadOnly` attribute for memory entries stored by an agent operating in that thread. `threadOnly: true` (default) restricts an entry's visibility to its originating thread; `threadOnly: false` makes it visible to that agent across its other threads.

**Tier 1 (screening)**
The first tier of the initiative cognition model. A small, locally-hosted LLM performs fast, cheap screening of observed events to decide whether the agent's primary LLM should be invoked.

**Tier 2 (reflection)**
The second tier of the initiative cognition model. The agent's primary LLM performs full cognition (perceive, reflect, decide, act, learn). Invoked selectively, only on Tier-1 "act" verdicts.

**Timeline**
The ordered, append-only record of all artifacts within a thread: messages, `ParticipantStateChanged` events, retractions, and system events. Corrections and retractions are new Timeline events that reference prior artifacts, not in-place mutations. Per-thread FIFO is the ordering invariant.

**Tool**
The runtime-invocation surface an agent calls — a concrete, named, schema-typed action, id `<namespace>.<tool_name>`. Distinct from a skill (authored prose). Tools reach an agent in three tiers: platform (`sv.*`), connector (`<connector-slug>.*`), and image. See [Tools](concepts/tools.md).

**Topic**
A named pub/sub channel for event distribution. Topic names are namespaced `{tenant-id}/{owner-id}/{topic}`; system topics use the `system/` prefix.

**Unit**
An agent that has children. A unit IS an agent: it has a mailbox, an execution config, and a runtime invocation path, with membership, expertise aggregation, connector binding, and a boundary added on top. When a message reaches a unit, the unit's own runtime runs. See [Units](concepts/units.md) and [ADR-0053](decisions/0053-units-are-agents-and-one-way-delivery.md).

**UnitActor**
The Dapr virtual actor implementing a unit. Owns the same mailbox contract as an agent, plus member dispatch; the member graph itself is EF-authoritative.

**UnitPolicy**
The governance record on a unit — optional slots constraining member agents (skill, model, cost, execution-mode, initiative). A unit is a trust boundary: a unit policy cannot be escaped by a per-membership or per-agent override.

**Workflow**
A durable, structured execution plan. Workflow-driven agent runtimes ship as their own container images; the platform's internal lifecycle workflows (validation, agent creation, cloning) run as Dapr Workflows in the worker.
