# Spring Voyage — Architecture Plan

**Status:** Living document — kept in sync with the implementation.
**Last reviewed:** 2026-04-21

This document is the canonical architecture index. The narrow, dated decisions behind each major choice live as Architecture Decision Records under [`docs/decisions/`](../decisions/README.md); reach for those when you want the "why" without re-reading the entire plan.

---

## 1. Context & Problem Statement

### What v1 Taught Us

Spring Voyage v1 is a working proof-of-concept: Claude-powered agents collaborating on GitHub repositories. It works — but its architecture has fundamental limitations that prevent it from becoming the general-purpose AI team platform it needs to be.

**v1 limitations driving v2:**

1. **GitHub-centric by design.** The entire model — issues as work items, labels as state, PRs as output, webhooks as events — is hardwired to software engineering on GitHub. A product management team working with documents, a creative team working with Figma, a people management team working with messages — none of these fit v1's model.
2. **Flat team structure only.** v1 has one organizational unit: a team of expert agents with a team leader. No nested teams, no communities, no ad-hoc gatherings, no self-organizing groups.
3. **Single-human interaction model.** v1 assumes one user. Multiple humans interacting with the same agent or team — with different permission levels — isn't supported.
4. **Brittle, baked-in state machine.** The v1 state machine (READY → IN_PROGRESS → DONE with label-based transitions) is fragile and hardcoded. Different work patterns need different workflows — from explicit step-by-step orchestration to fully LLM-driven coordination.
5. **Poor observability.** Observing agent work and errors requires admin platform access and log diving. No structured activity streams, no agent-to-agent observation, no dashboard-level insight into what agents are thinking and deciding.
6. **Multi-tenancy as afterthought.** No clean tenant upgrades, no tenant-scoped access control, single-user assumption throughout.
7. **Human-in-the-loop only.** v1 agents wait for assignments, do work, wait for review. No initiative, no continuous operation, no autonomous decision-making.
8. **Python runtime issues.** Despite thousands of tests, many errors surface only at runtime. Type safety at the infrastructure/plumbing layer would reduce this class of bugs.
9. **Manual concurrency scaling.** To handle concurrent work of the same type, v1 required defining multiple identical agents (e.g., three backend engineers). If all were busy, work queued. No dynamic scaling, no elasticity.

### What v1 Got Right (and we carry forward)

- The core insight: AI agents collaborating through external tools (Claude Code) with worktree isolation
- Brain/Hands separation (implicit in v1, explicit in v2)
- The general flow: receive work → plan → execute → deliver → learn
- Agent memory (persistent learning across conversations)
- Prompt assembly patterns
- Web dashboard concept (activity feed, agent status, analytics)

---

## 2. Vision & Goals

Spring Voyage is an open-source collaboration platform for teams of AI agents — and the humans they work with. It is a substrate for standing up small fleets of AI collaborators that operate on real work, on the real systems where that work happens, with people in the loop where it counts.

Autonomous AI agents — organized into composable groups called **units** — collaborate with each other and with humans on any domain: software engineering, product management, creative work, research, operations, and more. Multiple humans participate in the same unit at different permission levels, and threads ([§ Engagements / Collaborations](../concepts/threads.md)) are the durable shared spaces where humans and agents converse, coordinate, and get work done over time.

Agents connect to external systems through pluggable **connectors**, communicate via typed **messages**, take **initiative** to act autonomously, and can be observed by humans and other agents in real-time.

**Orchestration is a mechanism, not the goal.** Each unit picks an orchestration strategy that decides how it routes work across its members ([§ Orchestration](orchestration.md)). The platform supports multiple strategies (rule-based, workflow, AI-driven, label-routed, peer) plus external orchestrators over A2A, but orchestration is one part of the substrate that supports collaboration — not the headline category Spring Voyage occupies.

### Design Goals

Each goal directly addresses a v1 limitation:


| Goal                                                                                               | Addresses                                 |
| -------------------------------------------------------------------------------------------------- | ----------------------------------------- |
| **Domain-agnostic.** Agents work with any external system, not just GitHub.                        | v1 limitation #1 (GitHub-centric)         |
| **Composable.** Units nest recursively. A unit appears as a single agent to its parent.            | v1 limitation #2 (flat teams)             |
| **Multi-human.** Multiple humans interact with agents at different permission levels.              | v1 limitation #3 (single-human)           |
| **Flexible orchestration in service of collaboration.** From rigid workflows to fully autonomous — each unit chooses how its members route work. | v1 limitation #4 (brittle state machine)  |
| **Observable.** Humans see what agents are doing, thinking, deciding, and spending — in real-time. | v1 limitation #5 (poor observability)     |
| **Extensible for multi-tenancy.** Designed for clean isolation and scoped access via extensions.   | v1 limitation #6 (afterthought)           |
| **Self-organizing.** Agents take initiative, operate continuously, and make autonomous decisions.  | v1 limitation #7 (human-in-the-loop only) |
| **Type-safe infrastructure.** .NET infrastructure layer with Dapr building blocks.                 | v1 limitation #8 (Python runtime issues)  |
| **Elastic.** Agents clone dynamically to handle concurrent work. No manual agent duplication.      | v1 limitation #9 (manual scaling)         |
| **Cost-aware.** Every LLM call, every initiative reflection, every action has a tracked cost.      | New requirement                           |


---

## 3. Terminology


| Term                      | Description                                                                                                                  |
| ------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| **Agent**                 | An autonomous AI-powered entity. Not necessarily a "worker" — can be an observer, advisor, monitor, researcher.              |
| **Unit**                  | A group of agents performing together. A unit IS an agent (composite pattern). Contains agents and/or other units.           |
| **Connector**             | A pluggable adapter bridging an external system (GitHub, Slack, Figma, etc.) to the unit.                                    |
| **Message**               | A typed communication between addressable entities.                                                                          |
| **Address**               | A globally-unique routable identity. Shape: `(Scheme, Guid)`; canonical wire form `scheme:<32-hex-no-dash>`. See [Identifiers](identifiers.md). |
| **Topic**                 | A named pub/sub channel for event distribution.                                                                              |
| **Package**               | An installable bundle of skills, connectors, workflows, templates, or config.                                                |
| **Activation**            | What causes an agent to wake up and act.                                                                                     |
| **Workflow**              | A durable, structured execution plan for a unit.                                                                             |
| **Directory**             | A registry of agent expertise, queryable within and across units.                                                            |
| **Mailbox**               | An agent's inbound message system with prioritized channels.                                                                 |
| **Initiative**            | An agent's capacity to autonomously decide to act without external triggers.                                                 |


---

## Architecture Documents

| Document | Topics |
| --- | --- |
| [Infrastructure](infrastructure.md) | Dapr building blocks, actor model, IAddressable, data persistence |
| [Identifiers](identifiers.md) | Single-identity model: Guid identity, wire forms (no-dash hex / dashed JSON), `Address` shape, OSS default tenant id, manifest grammar, CLI search-with-context |
| [Messaging](messaging.md) | Mailbox, message processing, addressing, activation model |
| [Units & Agents](units.md) | Unit entity model (identity, membership, nested units, composite pattern); entry point to the units-and-agents cluster |
| [Agents](agents.md) | Agent model, execution pattern, cloning policies, role, prompt assembly, platform tools |
| [Orchestration](orchestration.md) | Orchestration strategies (AI, workflow, label-routed), unit boundary, execution-defaults resolution chain |
| [Policies](policies.md) | Unit policy framework (skill, model, cost, execution mode, initiative), root unit |
| [Expertise](expertise.md) | Expertise profiles, directory, recursive aggregation, directory search, YAML seeding |
| [Unit Lifecycle](unit-lifecycle.md) | Status DAG, validation workflow, imperative and declarative creation paths, observe and teardown |
| [Initiative](initiative.md) | Initiative levels, tiered cognition, initiative policies |
| [Workflows](workflows.md) | Workflow-as-container, platform-internal workflows, A2A execution dispatch, agent tool launchers, A2A sidecar protocol, workflow patterns |
| [Agent Runtime](agent-runtime.md) | A2A dispatcher tiers, launcher contract, MCP callback, Dapr Conversation provider/model YAML contract (Ollama / OpenAI / Anthropic / Google), adding a new launcher |
| [Agent Credential Rotation](agent-credential-rotation.md) | Design rationale for D1 spec § 2.2.3 — restart-as-rotation-primitive, supervisor re-injection via `IAgentContextBuilder`, future evolution to mounted-files + refresher |
| [Connectors](connectors.md) | Connector model, skills, implementation tiers |
| [Observability](observability.md) | Activity events, Rx.NET streams, cost tracking |
| [CLI & Web](cli-and-web.md) | Client API surface, hosting modes, CLI, deployment topology |
| [Configuration](configuration.md) | Startup configuration validation framework (`IConfigurationRequirement`, report shape, fail-fast policy) |
| [Deployment](deployment.md) | Agent hosting modes (ephemeral vs persistent), persistent agent registry lifecycle, container runtime requirements, Dapr sidecar bootstrap, tenant-scoped runtime topology, solution structure |
| [Security](security.md) | Authentication, permissions, multi-tenancy, secrets stack (registry / store / resolver, at-rest encryption, rotation, unit → tenant inheritance, audit logging) |
| [Packages](packages.md) | Domain packages, skill format, package system |
| [Open Questions](open-questions.md) | Open design questions, future work |

For the phased implementation plan, see [`docs/roadmap/`](../roadmap/README.md).
