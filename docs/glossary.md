# Glossary

Definitions of key terms used throughout the Spring Voyage documentation.

**Spring Voyage** is an open-source collaboration platform for teams of AI agents — and the humans they work with. Throughout this glossary, "platform" refers to Spring Voyage; "the substrate" refers to the same thing from the operator's vantage. Orchestration is one mechanism inside a unit; collaboration between humans and agents is the larger frame the platform exists to make tractable.

---

**A2A (Agent-to-Agent)**
An open protocol for cross-framework agent communication. Enables Spring agents to collaborate with agents built on other frameworks (Google ADK, LangGraph, etc.).

**Activation**
What causes an agent to wake up and act. Activation triggers include direct messages, pub/sub subscriptions, scheduled reminders, volatile timers, external events (via bindings), workflow steps, and initiative.

**Address**
A globally unique routable identity for any addressable entity. Shape: `(Scheme, Guid)` — a scheme like `agent`, `unit`, `human`, or `connector` plus the addressed actor's stable `Guid` identity. Canonical wire form: `scheme:<32-hex-no-dash>` (e.g. `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7`). Parsers are lenient (the dashed Guid form is accepted everywhere); the emit form is uniform. There is no path-shaped address, no `@<uuid>` form, and no namespace+name pair — identity is the `Guid`. The membership graph is the addressing fabric (a unit's members are reached by walking the graph from the addressed actor toward the tenant root); presentation rendering uses `display_name` resolved at read time and never participates in routing. See [Identifiers](architecture/identifiers.md), [ADR-0036](decisions/0036-single-identity-model.md), and the `display_name` validator in `Cvoya.Spring.Core.Validation.DisplayNameValidator`.

**Agent**
An autonomous AI-powered entity. The fundamental building block of the platform. Can be a worker, observer, advisor, monitor, researcher, or any other role. Every agent has an identity, can receive messages, and can reason about how to respond.

**Agent Actor (AgentActor)**
The Dapr virtual actor implementing an agent. Manages runtime state, AI cognition, pub/sub subscriptions, and the mailbox.

**AgentMemory**
The agent's single, ordered, append-only memory store. Entries are **MemoryEntry** records with optional `thread_id` and `threadOnly` attributes. When the agent is operating in any thread, it reads the visible subset — entries whose `thread_id == current_thread`, or `threadOnly == false`, or `thread_id == null` (thread-less). Writes go through the `store(memory)` MCP tool; reads through `recall(query)`. Visibility is governed at write time by the thread's **ThreadMemoryPolicy**. See `docs/concepts/threads.md` for positioning, `docs/architecture/thread-model.md` § Q4 for design detail, and ADR-0030 for the durable decision.

**AgentThreadMemory**
**Superseded — was a separate-store framing in an early F1 draft.** The current model (see `docs/architecture/thread-model.md` § Q4) collapses to a single **AgentMemory** store with per-entry `thread_id` and `threadOnly` attributes; the term "per-thread memory" is now informally a filter view over `AgentMemory` (the entries with `thread_id == T`).

**Boundary**
The interface a unit exposes when acting as a member of a parent unit. Controls what is visible (transparent, translucent, or opaque) and what operations are projected, filtered, or synthesized.

**Clone**
A platform-managed copy of an agent, spawned to handle concurrent work. Governed by the agent's cloning policy (none, ephemeral-no-memory, ephemeral-with-memory, persistent) and attachment mode (detached, attached).

**Cognition Loop**
The five-step reasoning process agents use during initiative: Perceive, Reflect, Decide, Act, Learn.

**Collaboration**
The active shared space where participants converse, coordinate, and get work done. The UX active-workspace surface — what a user opens to do something today. Recorded by the system as a **Thread** and presented in product navigation as an **Engagement**. Example phrasing: "Open collaboration with the writing agent." See `docs/concepts/threads.md` for positioning across the three layers (Thread / Engagement / Collaboration).

**Connector**
A pluggable adapter bridging an external system (GitHub, Slack, Figma, etc.) to a unit. Provides event translation (external events become platform messages) and skills (capabilities agents can use).

**Connector Actor (ConnectorActor)**
The Dapr virtual actor implementing a connector. Manages event translation, outbound skills, and connection lifecycle.

**Conversation**
**Superseded by Thread.** The pre-v0.1 term for what is now a thread. v0.1 leaves a free hand on schema and API change (see `docs/architecture/thread-model.md` § Q10 — no migration, no legacy partition), so there is no `ConversationId` field surviving on disk; the rename is uniform. See **Thread**.

**Conversation Channel**
**Superseded — now described as the per-thread queue inside the agent's mailbox.** See `docs/architecture/messaging.md` (F2 will refresh) and **Thread**.

**Control Channel**
A partition of the agent's mailbox for platform control messages (Cancel, StatusQuery, HealthCheck, PolicyUpdate). Control messages are never blocked behind work.

**Dapr**
A distributed application runtime providing building blocks (actors, pub/sub, state, secrets, workflows) as a sidecar process. The infrastructure foundation of Spring Voyage.

**Delegated Execution**
An execution pattern where the agent actor dispatches work to an isolated execution environment (container) running a tool like Claude Code. The tool drives its own agentic loop.

**Directory**
A registry of agent expertise, queryable within and across units. Each unit maintains its members' expertise profiles. Directories compose recursively through the unit hierarchy.

**display_name**
The human-facing label for an actor (unit, agent, human, connector, tenant) used in wizard listings, activity-log narrative, drawer panels, and CLI table output. Not unique, not addressable, not a foreign-key target. The platform rejects any `display_name` that round-trips through `Guid.TryParseExact` for any standard form, so a token that looks Guid-shaped is unambiguously identity, not a name. CLI surfaces accept `display_name` as **search input** (returning 0/1/n results); they never accept it as a routed lookup. See [Identifiers](architecture/identifiers.md), [ADR-0036 § 2](decisions/0036-single-identity-model.md), and `Cvoya.Spring.Core.Validation.DisplayNameValidator`.

**Domain Package**
See Package.

**Engagement**
The ongoing shared context between participants over time. The UX product-narrative term for the enduring relationship between participants — used in product navigation, lists, and continuity-of-relationship copy. Recorded by the system as one or more **Threads** (a participant-set change produces a new thread; the engagement absorbs the transition) and worked in as a **Collaboration**. Example phrasing: "Continue this engagement." See `docs/concepts/threads.md` for positioning across the three layers (Thread / Engagement / Collaboration).

**Execution Environment**
An isolated runtime (container) where a delegated agent's work runs. Separate from the agent actor. Sandboxed by default.

**Expertise Profile**
A structured description of what an agent knows and how well it knows it. Seeded from configuration, optionally evolved through observation and learning.

**Human Actor (HumanActor)**
The Dapr virtual actor representing a human participant. Routes notifications and enforces permission levels.

**IAddressable**
The foundational interface: "I have an address." Every entity that participates in the platform implements this.

**IMessageReceiver**
The core messaging interface: "I can receive messages." Extends IAddressable. Implemented by all four actor types.

**Initiative**
An agent's capacity to autonomously decide to act without external triggers. Ranges from Passive (no initiative) to Autonomous (full self-direction). Governed by unit-level policies.

**Mailbox**
An agent's inbound message system, logically partitioned into control, conversation, and observation channels.

**MemoryEntry**
A single record in an agent's **AgentMemory**. Shape: `{ id, timestamp, payload, thread_id?, threadOnly? }`. The `payload` may be any kind of memory artifact (fact, lesson, generalised pattern, observation, reasoning step, etc.) — the platform stores them uniformly and the agent's cognition decides what each represents. The `threadOnly` attribute is stamped at write time from the thread's **ThreadMemoryPolicy** and controls cross-thread visibility for the entry. See `docs/concepts/threads.md` for positioning, `docs/architecture/thread-model.md` § Q4 for design detail, and ADR-0030 for the durable decision.

**MemoryPromotionPolicy**
**Superseded by ThreadMemoryPolicy.** The prior draft's framing of "promotion between two stores" is replaced by "visibility attribute on a single store"; the underlying intent — a per-thread privacy knob — is preserved under the new name. See `docs/architecture/thread-model.md` § Q4.

**Message**
A typed communication between addressable entities. Contains an ID, sender, recipient, type, conversation ID, payload, and timestamp.

**Observation Channel**
A partition of the agent's mailbox for events from subscriptions, timers, and observed agents. Processed in batch by the initiative cognition loop.

**OssTenantIds.Default**
The deterministic v5 UUID owning every tenant-scoped row in a fresh OSS install: `dd55c4ea-8d72-5e43-a9df-88d07af02b69` (no-dash form: `dd55c4ea8d725e43a9df88d07af02b69`). Computed once over namespace `00000000-0000-0000-0000-000000000000` and label `cvoya/tenant/oss-default`, pinned as a literal in `src/Cvoya.Spring.Core/Tenancy/OssTenantIds.cs`. Recomputable from outside the platform via any v5 implementation. The class also exposes `DefaultDashed` and `DefaultNoDash` `const string` literals for grep-ability across configuration files, dashboards, and audit logs. See [Identifiers § 5](architecture/identifiers.md#5-the-oss-default-tenant-id) and [ADR-0036 § 8](decisions/0036-single-identity-model.md).

**Observer**
An agent that subscribes to another agent's activity stream (with permission).

**Orchestration Strategy**
A pluggable component that determines how a unit routes incoming messages to its members. Orchestration is one mechanism inside a unit's collaboration model — it sits alongside membership, the boundary, policies, and the activity stream, and answers "which member handles this message?" Five strategies ship in the platform: Rule-based, Workflow, AI-orchestrated, AI+Workflow hybrid, Peer. External orchestrators (ADK, LangGraph, Temporal, …) participate via A2A.

**Package**
An installable bundle of domain-specific content: agent templates, unit templates, skills, workflows, connectors, and execution environments. How the platform remains domain-agnostic while supporting specific domains.

**Skill**
A bundle of a prompt fragment (`.md`) and optional tool definitions (`.tools.json`). The smallest unit of reusable domain knowledge.

**Tenant**
An isolated organizational unit. Contains a root unit, users, and resources. Maps to a Dapr namespace. The top-level boundary for access control, billing, and resource isolation.

**Tier 1 (Screening)**
The first tier of the initiative cognition model. A small, locally-hosted LLM performs fast, cheap screening of events to decide whether the agent's primary LLM should be invoked.

**Tier 2 (Reflection)**
The second tier of the initiative cognition model. The agent's primary LLM (Claude, GPT-4, etc.) performs full cognition: perceive, reflect, decide, act, learn. Invoked selectively.

**Thread**
The unique, persistent, system-level record for a set of two or more participants, containing their lifelong shared exchanges and activity. The participant set IS the identity: there is exactly one thread per unique participant set; adding or removing a participant produces a different thread. This is the system / architectural concept — used in code, schema, APIs, and architecture docs. Users do not see threads directly: the product presents the thread as an **Engagement**, and the user works inside it as a **Collaboration**. See `docs/concepts/threads.md` for positioning across the three layers, `docs/architecture/thread-model.md` for design detail, and ADR-0030 for the durable decision.

**ThreadMemoryPolicy**
Per-thread policy that sets the default `threadOnly` attribute for memory entries (**MemoryEntry**) stored by an agent operating in that thread. `threadOnly: true` (default) restricts the entry's visibility to the originating thread; `threadOnly: false` allows the entry to be visible to that agent across its other threads. The only memory-flow knob in v0.1. See `docs/concepts/threads.md` for positioning, `docs/architecture/thread-model.md` § Q4 for design detail, and ADR-0030 for the durable decision.

**Timeline**
The ordered, timestamped record of all artifacts within a thread: messages (user / agent / initiative), task lifecycle events, **ParticipantStateChanged** events, retractions, and system events. Append-only at the platform level; corrections and retractions are new Timeline events that reference prior artifacts, not in-place mutations. Per-thread FIFO is the ordering invariant. See `docs/architecture/thread-model.md` § Q7.

**Topic**
A named pub/sub channel for event distribution. Topic names are namespaced by tenant + owner Guid + topic name (`{tenant-id}/{owner-id}/{topic}`); system topics use the `system/` prefix.

**Unit**
A group of agents — and the humans who work with them — performing together. A unit IS an agent (composite pattern) -- it implements the same interfaces and can contain agents and/or other units recursively. Each unit picks an orchestration strategy that decides how it routes work across its members; humans participate as Owners, Operators, or Viewers via the unit's permission model.

**Unit Actor (UnitActor)**
The Dapr virtual actor implementing a unit. Manages membership, policies, the expertise directory, and delegates to the orchestration strategy.

**Workflow**
A durable, structured execution plan. Domain workflows run in containers; platform-internal workflows run in the host process.
