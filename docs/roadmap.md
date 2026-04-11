# Roadmap

Spring Voyage V2 is developed in six phases. Each phase delivers a complete, usable increment. Later phases build on earlier ones but don't invalidate them.

---

## Phase 1: Platform Foundation + Software Engineering Domain

The foundation. Everything else builds on this.

**Status: Complete** (3 remaining items tracked below)

**What ships:**
- [x] .NET host with Dapr actors (AgentActor, UnitActor, ConnectorActor, HumanActor)
- [x] IAddressable / IMessageReceiver + message routing (flat units)
- [x] AI-orchestrated + Workflow orchestration strategies
- [x] Platform-internal Dapr Workflows for agent lifecycle and cloning lifecycle
- [x] Partitioned mailbox with conversation suspension
- [x] Four-layer prompt assembly (platform, unit context, conversation context, agent instructions)
- [x] `checkMessages` platform tool for delegated agent message retrieval
- [x] One connector: GitHub (C#)
- [x] Brain/Hands: hosted + delegated execution
- [x] Address resolution: cached directory with event-driven invalidation, permission checks at resolution time
- [x] Basic API host (with single-user local dev mode), CLI (`spring` command)
- [x] Skill format: prompt fragments + optional tool definitions, composable via declaration order
- [x] `software-engineering` domain package (agent templates, unit templates, skills, workflow container)
- [ ] Workflow-as-container deployment with Dapr sidecars
- [ ] Dapr state store wrapper integration

**Milestone:** v1 feature parity on the new architecture.

---

## Phase 2: Observability + Multi-Human

Real-time visibility into what agents are doing, and support for multiple human participants.

**Status: Planning complete, not started**

**What ships:**
- [ ] Enrich ActivityEvent model + Rx.NET pipeline
- [ ] Streaming event types + Dapr pub/sub transport
- [ ] Basic cost tracking service + aggregation
- [ ] Multi-human RBAC with unit-scoped permissions
- [ ] Clone state model + ephemeral lifecycle
- [ ] Clone API endpoints + cost attribution
- [ ] Real-time SSE endpoint + activity query API
- [ ] React/Next.js web dashboard

**Delivers:** Real-time observation of agent work, multi-human participation, elastic agent scaling.

---

## Phase 3: Initiative + Product Management Domain

Agents start taking initiative. A second domain proves the platform is genuinely domain-agnostic.

**Status: Not started**

**What ships:**
- [ ] Passive + Attentive initiative levels
- [ ] Tier 1 screening (small LLM), Tier 2 reflection
- [ ] Initiative policies, event-triggered cognition
- [ ] Cancellation flow (CancellationToken propagation to execution environments)
- [ ] `product-management` domain package with second connector (Linear, Notion, or Jira)

**Delivers:** Agents that take initiative; second domain proves platform generality.

---

## Phase 4: A2A + Additional Strategies

Cross-framework interoperability and the full orchestration strategy spectrum.

**Status: Not started**

**What ships:**
- [ ] A2A protocol support (external agents as unit members, external orchestrators)
- [ ] Rule-based and Peer orchestration strategies
- [ ] External workflow engine integration via A2A (ADK, LangGraph as orchestrators)

**Delivers:** Full orchestration strategy spectrum, cross-framework agent collaboration.

---

## Phase 5: Unit Nesting + Directory + Boundaries

Organizational structure beyond flat teams.

**Status: Not started**

**What ships:**
- [ ] Recursive composition (units containing units)
- [ ] Expertise directory and aggregation
- [ ] Unit boundary (opacity, projection, filtering, synthesis)
- [ ] Flat routing with hierarchy-aware permission checks

**Delivers:** Complex organizational structures with hierarchy-aware routing.

---

## Phase 6: Platform Maturity

Production-grade multi-organization platform.

**Status: Not started**

**What ships:**
- [ ] Package system (local registry, install, versioning)
- [ ] `research` domain package and additional connectors

**Delivers:** Formal package distribution and additional domain packages.

---

## Future Work (Beyond Phase 6)

The architecture is designed to accommodate these capabilities. Interfaces and extension points are in place.

**Dynamic Agent and Unit Creation** — Agents and units created programmatically at runtime: workload scaling, specialist spawning, ad-hoc units, emergent structure.

**Advanced Self-Organization** — Agents negotiating task allocation, forming ad-hoc sub-units, and reorganizing unit structure based on workload patterns.
