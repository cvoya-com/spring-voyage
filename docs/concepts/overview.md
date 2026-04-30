# Spring Voyage -- Concepts Overview

Spring Voyage is an open-source collaboration platform for teams of AI agents -- and the humans they work with. It is a substrate for standing up small fleets of AI collaborators that operate on real work, on the real systems where that work happens, with people in the loop where it counts.

Autonomous AI agents -- organized into composable groups called **units** -- collaborate with each other and with humans on any domain: software engineering, product management, creative work, research, operations, and more.

This document series describes the core concepts and abstractions that make up the Spring Voyage model. No code is shown here -- these documents focus on *what* the system is, not *how* it is built.

## Where Orchestration Fits

*Orchestration* is one mechanism a unit can use to route work across its members. Spring Voyage's bet is that **collaboration** is the bigger category -- the part that's still genuinely under-explored -- and orchestration is one piece of how the platform supports it.

Concretely, every unit picks an orchestration strategy. The strategy decides which of the unit's members handles an incoming message — see [Units § Orchestration](units.md#orchestration-a-mechanism-inside-the-unit) for the catalogue. External orchestrators (ADK, LangGraph, Temporal, …) participate over A2A. But routing is only one slice of what happens inside a unit:

- **Humans participate as first-class members**, not just as observers. Multiple humans can be Owners, Operators, or Viewers on the same unit, ask the unit clarifying questions, answer questions the unit asks back, and intervene mid-work.
- **Engagements / collaborations** are the durable shared spaces where work happens over time -- see [Threads, Engagements, and Collaborations](threads.md). The platform records each shared space as a thread keyed by the participant set; the user works in it as a collaboration.
- **Activity streams** make every agent's reasoning, decisions, and cost observable to humans and other agents in real-time.
- **Initiative** lets agents act on what they observe rather than only respond to triggers.
- **Boundaries** let a unit expose a deliberate face to the outside while preserving deep access for permitted humans.

Orchestration sits inside that stack. It answers "how should this unit route this message right now?" -- not "what is Spring Voyage for?"

## Document Map

| Document | Description |
|----------|-------------|
| [Agents](agents.md) | The autonomous AI entities at the heart of the platform |
| [Units](units.md) | Composable groups of agents that act as a single entity |
| [Messaging and Addressing](messaging.md) | How entities communicate and are identified |
| [Threads, Engagements, and Collaborations](threads.md) | The participant-set model: system concept, product narrative, working surface |
| [Connectors](connectors.md) | Pluggable bridges to external systems |
| [Initiative](initiative.md) | How agents autonomously decide to act |
| [Observability](observability.md) | Real-time visibility into agent activity, cost, and decisions |
| [Packages and Skills](packages.md) | Reusable bundles of domain knowledge and capabilities |
| [Tenants and Permissions](tenants.md) | Multi-tenancy, access control, and organizational isolation |

## Core Principles

**Domain-agnostic.** The platform knows nothing about software engineering, product management, or any specific domain. Domain knowledge lives in packages -- bundles of agent templates, skills, workflows, and connectors. The platform provides the primitives; packages provide the expertise.

**Composable.** Units nest recursively. A unit of three agents appears as a single agent to its parent unit. An engineering team, a product squad, and a research cell can all be members of a larger organization -- each hiding its internal complexity behind a clean boundary.

**Observable.** Every agent emits a structured activity stream. Humans and other agents can subscribe to these streams in real-time. Cost tracking is built in -- every LLM call, every action has a tracked cost.

**Self-organizing.** Agents don't just respond to triggers -- they can take initiative. An agent watching commit activity might notice untested code and proactively start writing tests. Initiative levels range from fully passive to fully autonomous, governed by configurable policies.

**Elastic.** When an agent is busy and new work arrives, the platform can spawn clones to handle concurrent work. Clones are governed by policies -- some are ephemeral (destroyed after one task), others persist and evolve independently.
