# Roadmap

Spring Voyage V2 is developed in six phases. Each phase delivers a complete, usable increment. Later phases build on earlier ones but don't invalidate them.

---

## Phase 1: Platform Foundation + Software Engineering Domain

The foundation. Everything else builds on this.

**What ships:**
- .NET host with Dapr actors (AgentActor, UnitActor, ConnectorActor, HumanActor)
- IAddressable / IMessageReceiver + message routing (flat units)
- IOrchestrationStrategy with three implementations: AI-orchestrated, Workflow (container-based), AI+Workflow hybrid
- Workflow-as-container model: domain workflows deployed as containers with Dapr sidecars
- Platform-internal Dapr Workflows for agent lifecycle and cloning lifecycle
- Partitioned mailbox with conversation suspension
- Four-layer prompt assembly (platform, unit context, conversation context, agent instructions)
- `checkMessages` platform tool for delegated agent message retrieval
- One connector: GitHub (C#)
- Brain/Hands: hosted + delegated execution
- User authentication (OAuth via web portal, API token management)
- Address resolution: cached directory with event-driven invalidation, permission checks at resolution time
- Basic API host (with single-tenant local dev mode), CLI (`spring` command)
- PostgreSQL via Dapr state store + direct EF Core
- Skill format: prompt fragments + optional tool definitions, composable via declaration order
- `software-engineering` domain package (agent templates, unit templates, skills, workflow container)

**Milestone:** v1 feature parity on the new architecture.

---

## Phase 2: Observability + Multi-Human

Real-time visibility into what agents are doing, and support for multiple human participants.

**What ships:**
- Structured activity events via IObservable/ActivityEvent (Rx.NET)
- Streaming from execution environments (TokenDelta, ToolCall events)
- Cost tracking per agent/unit/tenant
- Multi-human RBAC (owner, operator, viewer)
- Agent cloning: ephemeral-no-memory and ephemeral-with-memory policies, detached and attached modes
- Web dashboard v2

**Delivers:** Real-time observation of agent work, multi-human participation, elastic agent scaling.

---

## Phase 3: Initiative + Product Management Domain

Agents start taking initiative. A second domain proves the platform is genuinely domain-agnostic.

**What ships:**
- Passive + Attentive initiative levels
- Tier 1 screening (small LLM), Tier 2 reflection
- Initiative policies, event-triggered cognition
- Cancellation flow (CancellationToken propagation to execution environments)
- `product-management` domain package with second connector (Linear, Notion, or Jira)

**Delivers:** Agents that take initiative; second domain proves platform generality.

---

## Phase 4: A2A + Additional Strategies

Cross-framework interoperability and the full orchestration strategy spectrum.

**What ships:**
- A2A protocol support (external agents as unit members, external orchestrators)
- Rule-based and Peer orchestration strategies
- External workflow engine integration via A2A (ADK, LangGraph as orchestrators)

**Delivers:** Full orchestration strategy spectrum, cross-framework agent collaboration.

---

## Phase 5: Unit Nesting + Directory + Boundaries

Organizational structure beyond flat teams.

**What ships:**
- Recursive composition (units containing units)
- Expertise directory and aggregation
- Unit boundary (opacity, projection, filtering, synthesis)
- Flat routing with hierarchy-aware permission checks
- Proactive + Autonomous initiative levels
- Persistent cloning policy (independent clone evolution, recursive cloning)

**Delivers:** Complex organizational structures, full initiative spectrum, full cloning spectrum.

---

## Phase 6: Platform Maturity

Production-grade multi-organization platform.

**What ships:**
- Package system (registry, install, versioning, NuGet distribution)
- `research` domain package and additional connectors
- Multi-tenancy hardening
- Federation (if needed)

**Delivers:** Production-grade multi-org platform with formal package distribution.

---

## Future Work (Beyond Phase 6)

The architecture is designed to accommodate these capabilities. Interfaces and extension points are in place.

**Alwyse: Cognitive Backbone** -- An optional observer agent that acts as each agent's personal intelligence. Replaces default implementations with cognitive memory, pattern recognition, expertise evolution, and sub-agent spawning.

**Expertise Marketplace** -- Cross-unit expertise access with metered billing and SLA contracts.

**Dynamic Agent and Unit Creation** -- Agents and units created programmatically at runtime: workload scaling, specialist spawning, ad-hoc units, emergent structure.

**Cross-Organization Federation** -- Multiple Spring Voyage deployments federating expertise directories across organizational boundaries.

**Advanced Self-Organization** -- Agents negotiating task allocation, forming ad-hoc sub-units, and reorganizing unit structure based on workload patterns.
