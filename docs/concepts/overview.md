# Spring Voyage -- Concepts Overview

Spring Voyage is a general-purpose AI agent orchestration platform. It enables autonomous AI agents -- organized into composable groups called **units** -- to collaborate on any domain: software engineering, product management, creative work, research, operations, and more.

This document series describes the core concepts and abstractions that make up the Spring Voyage model. No code is shown here -- these documents focus on *what* the system is, not *how* it is built.

## Document Map

| Document | Description |
|----------|-------------|
| [Agents](agents.md) | The autonomous AI entities at the heart of the platform |
| [Units](units.md) | Composable groups of agents that act as a single entity |
| [Messaging and Addressing](messaging.md) | How entities communicate and are identified |
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
