# Roadmap

Spring Voyage V2 is developed in six phases. Each phase delivers a complete, usable increment. Later phases build on earlier ones but don't invalidate them.

Features are split between **OSS** (open-source `spring-voyage` repo) and **Private** (`spring-voyage-cloud` repo). The private repo consumes the OSS repo as a git submodule and extends it with multi-tenancy, advanced features, and hosted service infrastructure.

---

## Phase 1: Platform Foundation + Software Engineering Domain

The foundation. Everything else builds on this.

**Status: Complete** (3 remaining items tracked below)

**What ships:**
- [x] .NET host with Dapr actors (AgentActor, UnitActor, ConnectorActor, HumanActor) `[OSS]`
- [x] IAddressable / IMessageReceiver + message routing (flat units) `[OSS]`
- [x] AI-orchestrated + Workflow orchestration strategies `[OSS]`
- [x] Platform-internal Dapr Workflows for agent lifecycle and cloning lifecycle `[OSS]`
- [x] Partitioned mailbox with conversation suspension `[OSS]`
- [x] Four-layer prompt assembly (platform, unit context, conversation context, agent instructions) `[OSS]`
- [x] `checkMessages` platform tool for delegated agent message retrieval `[OSS]`
- [x] One connector: GitHub (C#) `[OSS]`
- [x] Brain/Hands: hosted + delegated execution `[OSS]`
- [x] Address resolution: cached directory with event-driven invalidation, permission checks at resolution time `[OSS]`
- [x] Basic API host (with single-user local dev mode), CLI (`spring` command) `[OSS]`
- [x] Skill format: prompt fragments + optional tool definitions, composable via declaration order `[OSS]`
- [x] `software-engineering` domain package (agent templates, unit templates, skills, workflow container) `[OSS]`
- [ ] Hybrid orchestration strategy (AI+Workflow) `[Private]`
- [ ] User authentication: OAuth via web portal (#761) `[Private]`
- [ ] Workflow-as-container deployment with Dapr sidecars (#762) `[OSS]`
- [ ] Dapr state store wrapper integration (#763) `[OSS]`

**Milestone:** v1 feature parity on the new architecture.

---

## Phase 2: Observability + Multi-Human

Real-time visibility into what agents are doing, and support for multiple human participants.

**Status: Planning complete, not started**

**What ships:**
- [ ] Enrich ActivityEvent model + Rx.NET pipeline (#764) `[OSS]`
- [ ] Streaming event types + Dapr pub/sub transport (#765) `[OSS]`
- [ ] Basic cost tracking service + aggregation (#766) `[OSS]`
- [ ] Advanced budget enforcement + alerting `[Private]`
- [ ] Multi-human RBAC with unit-scoped permissions (#767) `[OSS]`
- [ ] Multi-tenant user management + auth `[Private]`
- [ ] Clone state model + ephemeral lifecycle (#768) `[OSS]`
- [ ] Clone API endpoints + cost attribution (#769) `[OSS]`
- [ ] Real-time SSE endpoint + activity query API (#770) `[OSS]`
- [ ] React/Next.js web dashboard (#771) `[OSS]`
- [ ] Advanced analytics dashboard `[Private]`

**Dependency order:** #764 → {#765, #767, #768} (parallel) → #766 → #769 → #770 → #771

**Delivers:** Real-time observation of agent work, multi-human participation, elastic agent scaling.

---

## Phase 3: Initiative + Product Management Domain

Agents start taking initiative. A second domain proves the platform is genuinely domain-agnostic.

**Status: Not started**

**What ships:**
- [ ] Passive + Attentive initiative levels `[OSS]`
- [ ] Tier 1 screening (small LLM), Tier 2 reflection `[OSS]`
- [ ] Initiative policies, event-triggered cognition `[OSS]`
- [ ] Cancellation flow (CancellationToken propagation to execution environments) `[OSS]`
- [ ] `product-management` domain package with second connector (Linear, Notion, or Jira) `[OSS]`
- [ ] GitHub Copilot SDK integration for connector (#733) `[Private]`

**Delivers:** Agents that take initiative; second domain proves platform generality.

---

## Phase 4: A2A + Additional Strategies

Cross-framework interoperability and the full orchestration strategy spectrum.

**Status: Not started**

**What ships:**
- [ ] A2A protocol support (external agents as unit members, external orchestrators) `[OSS]`
- [ ] Rule-based and Peer orchestration strategies `[OSS]`
- [ ] External workflow engine integration via A2A (ADK, LangGraph as orchestrators) `[OSS]`

**Delivers:** Full orchestration strategy spectrum, cross-framework agent collaboration.

---

## Phase 5: Unit Nesting + Directory + Boundaries

Organizational structure beyond flat teams.

**Status: Not started**

**What ships:**
- [ ] Recursive composition (units containing units) `[OSS]`
- [ ] Expertise directory and aggregation `[OSS]`
- [ ] Unit boundary (opacity, projection, filtering, synthesis) `[OSS]`
- [ ] Flat routing with hierarchy-aware permission checks `[OSS]`
- [ ] Proactive + Autonomous initiative levels `[Private]`
- [ ] Persistent cloning policy (independent clone evolution, recursive cloning) `[Private]`

**Delivers:** Complex organizational structures, full initiative spectrum, full cloning spectrum.

---

## Phase 6: Platform Maturity

Production-grade multi-organization platform.

**Status: Not started**

**What ships:**
- [ ] Package system (local registry, install, versioning) `[OSS]`
- [ ] Hosted package registry (NuGet distribution) `[Private]`
- [ ] `research` domain package and additional connectors `[OSS]`
- [ ] Multi-tenancy hardening `[Private]`
- [ ] Audit logging (compliance-grade) `[Private]`
- [ ] Federation (if needed) `[Private]`

**Delivers:** Production-grade multi-org platform with formal package distribution.

---

## Future Work (Beyond Phase 6)

The architecture is designed to accommodate these capabilities. Interfaces and extension points are in place.

**Alwyse: Cognitive Backbone** `[Private]` — An optional observer agent that acts as each agent's personal intelligence. Replaces default implementations with cognitive memory, pattern recognition, expertise evolution, and sub-agent spawning.

**Expertise Marketplace** `[Private]` — Cross-unit expertise access with metered billing and SLA contracts.

**Dynamic Agent and Unit Creation** `[OSS]` — Agents and units created programmatically at runtime: workload scaling, specialist spawning, ad-hoc units, emergent structure.

**Cross-Organization Federation** `[Private]` — Multiple Spring Voyage deployments federating expertise directories across organizational boundaries.

**Advanced Self-Organization** `[OSS]` — Agents negotiating task allocation, forming ad-hoc sub-units, and reorganizing unit structure based on workload patterns.

---

## Open Source Strategy

**Repo model:** Two repositories with git submodule.

| Repo | Visibility | Purpose |
|------|-----------|---------|
| `spring-voyage` | Public | Core platform — agents, messaging, orchestration, connectors, CLI, dashboard |
| `spring-voyage-cloud` | Private | Hosted service — multi-tenancy, OAuth/SSO, billing, advanced features |

**License:** Decision pending — evaluating Apache 2.0, AGPL-3.0+CLA, and BSL 1.1. See #746.

**Extension model:** The private repo references OSS projects via submodule and overrides defaults via DI (tenant-scoped repositories, OAuth handlers, advanced strategies).

**Tracking:** Open-source preparation tracked in #752 (parent) with sub-issues #741-#751.
