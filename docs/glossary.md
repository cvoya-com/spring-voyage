# Glossary

Definitions of key terms used throughout the Spring Voyage documentation.

---

**A2A (Agent-to-Agent)**
An open protocol for cross-framework agent communication. Enables Spring agents to collaborate with agents built on other frameworks (Google ADK, LangGraph, etc.).

**Activation**
What causes an agent to wake up and act. Activation triggers include direct messages, pub/sub subscriptions, scheduled reminders, volatile timers, external events (via bindings), workflow steps, and initiative.

**Address**
A globally unique routable identity for any addressable entity. Comes in two forms: path addresses (human-readable, reflecting organizational hierarchy) and direct addresses (UUID-based, stable across moves).

**Agent**
An autonomous AI-powered entity. The fundamental building block of the platform. Can be a worker, observer, advisor, monitor, researcher, or any other role. Every agent has an identity, can receive messages, and can reason about how to respond.

**Agent Actor (AgentActor)**
The Dapr virtual actor implementing an agent. Manages runtime state, AI cognition, pub/sub subscriptions, and the mailbox.

**Boundary**
The interface a unit exposes when acting as a member of a parent unit. Controls what is visible (transparent, translucent, or opaque) and what operations are projected, filtered, or synthesized.

**Clone**
A platform-managed copy of an agent, spawned to handle concurrent work. Governed by the agent's cloning policy (none, ephemeral-no-memory, ephemeral-with-memory, persistent) and attachment mode (detached, attached).

**Cognition Loop**
The five-step reasoning process agents use during initiative: Perceive, Reflect, Decide, Act, Learn.

**Connector**
A pluggable adapter bridging an external system (GitHub, Slack, Figma, etc.) to a unit. Provides event translation (external events become platform messages) and skills (capabilities agents can use).

**Connector Actor (ConnectorActor)**
The Dapr virtual actor implementing a connector. Manages event translation, outbound skills, and connection lifecycle.

**Conversation**
A sequence of related messages identified by a ConversationId. Each agent works on one active conversation at a time, with others queued as pending.

**Conversation Channel**
A partition of the agent's mailbox holding messages for a specific conversation. At most one conversation channel is active at a time.

**Control Channel**
A partition of the agent's mailbox for platform control messages (Cancel, StatusQuery, HealthCheck, PolicyUpdate). Control messages are never blocked behind work.

**Dapr**
A distributed application runtime providing building blocks (actors, pub/sub, state, secrets, workflows) as a sidecar process. The infrastructure foundation of Spring Voyage.

**Delegated Execution**
An execution pattern where the agent actor dispatches work to an isolated execution environment (container) running a tool like Claude Code. The tool drives its own agentic loop.

**Directory**
A registry of agent expertise, queryable within and across units. Each unit maintains its members' expertise profiles. Directories compose recursively through the unit hierarchy.

**Domain Package**
See Package.

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

**Message**
A typed communication between addressable entities. Contains an ID, sender, recipient, type, conversation ID, payload, and timestamp.

**Observation Channel**
A partition of the agent's mailbox for events from subscriptions, timers, and observed agents. Processed in batch by the initiative cognition loop.

**Observer**
An agent that subscribes to another agent's activity stream (with permission).

**Orchestration Strategy**
A pluggable component that determines how a unit routes incoming messages to its members. Five strategies: Rule-based, Workflow, AI-orchestrated, AI+Workflow hybrid, Peer.

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

**Topic**
A named pub/sub channel for event distribution. Namespaced by unit.

**Unit**
A group of agents performing together. A unit IS an agent (composite pattern) -- it implements the same interfaces and can contain agents and/or other units recursively.

**Unit Actor (UnitActor)**
The Dapr virtual actor implementing a unit. Manages membership, policies, the expertise directory, and delegates to the orchestration strategy.

**Workflow**
A durable, structured execution plan. Domain workflows run in containers; platform-internal workflows run in the host process.
