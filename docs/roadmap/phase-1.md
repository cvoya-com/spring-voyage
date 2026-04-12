# Phase 1: Platform Foundation + Software Engineering Domain

> **[Roadmap Index](README.md)** | **Status: Complete**

The foundation. Everything else builds on this.

## Deliverables

- [x] .NET host with Dapr actors (AgentActor, UnitActor, ConnectorActor, HumanActor) — [Infrastructure](../architecture/infrastructure.md)
- [x] IAddressable / IMessageReceiver + message routing (flat units) — [Infrastructure](../architecture/infrastructure.md), [Messaging](../architecture/messaging.md)
- [x] AI-orchestrated + Workflow orchestration strategies — [Units & Agents](../architecture/units.md)
- [x] Platform-internal Dapr Workflows for agent lifecycle and cloning lifecycle — [Workflows](../architecture/workflows.md)
- [x] Partitioned mailbox with conversation suspension — [Messaging](../architecture/messaging.md)
- [x] Four-layer prompt assembly (platform, unit context, conversation context, agent instructions) — [Units & Agents](../architecture/units.md)
- [x] `checkMessages` platform tool for delegated agent message retrieval — [Units & Agents](../architecture/units.md)
- [x] One connector: GitHub (C#) — [Connectors](../architecture/connectors.md)
- [x] Brain/Hands: container-based delegated execution — [Units & Agents](../architecture/units.md)
- [x] Address resolution: cached directory with event-driven invalidation, permission checks at resolution time — [Messaging](../architecture/messaging.md)
- [x] Basic API host (with single-user local dev mode), CLI (`spring` command) — [CLI & Web](../architecture/cli-and-web.md)
- [x] Skill format: prompt fragments + optional tool definitions, composable via declaration order — [Packages](../architecture/packages.md)
- [x] `software-engineering` domain package (agent templates, unit templates, skills, workflow container) — [Packages](../architecture/packages.md)
- [x] Workflow-as-container deployment with Dapr sidecars — [Workflows](../architecture/workflows.md)
- [x] Dapr state store wrapper integration — [Infrastructure](../architecture/infrastructure.md)

**Milestone:** v1 feature parity on the new architecture.
