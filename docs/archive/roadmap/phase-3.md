# Phase 3: Initiative + Product Management Domain

> **Historical planning record.** This describes planned work; for the current system see [docs/architecture/](../../architecture/README.md). Kept for context.

> **[Roadmap Index](README.md)** | _Historical snapshot — live progress in the [V2 milestone](https://github.com/cvoya-com/spring-voyage/milestone/1) and umbrella [#418](https://github.com/cvoya-com/spring-voyage/issues/418)._

Agents start taking initiative. A product management domain package (templates only, no connector) proves the platform is domain-agnostic.

## Deliverables

- [x] Initiative types, policy model, and decision enums (#62) — [Initiative](../../architecture/units-and-agents.md)
- [x] ICognitionProvider interface for tiered screening (#63) — [Initiative](../../architecture/units-and-agents.md)
- [x] IInitiativeEngine interface (#64) — [Initiative](../../architecture/units-and-agents.md)
- [x] ICancellationManager interface (#65) — [Messaging](../../architecture/messaging.md)
- [x] InitiativeEngine implementation (#66) — [Initiative](../../architecture/units-and-agents.md)
- [x] Tier 1 CognitionProvider — Ollama (#67) — [Initiative](../../architecture/units-and-agents.md)
- [x] Tier 2 CognitionProvider — primary LLM (#68) — [Initiative](../../architecture/units-and-agents.md)
- [x] AgentActor initiative integration (#69) — [Initiative](../../architecture/units-and-agents.md)
- [x] CancellationManager + execution propagation (#70) — [Messaging](../../architecture/messaging.md)
- [x] Initiative API endpoints (#71) — [Initiative](../../architecture/units-and-agents.md)
- [x] DI registration for initiative services (#72) — [Initiative](../../architecture/units-and-agents.md)
- [x] Product management domain package — agent/unit/skill templates, no connector (#73) — [Packages](../../architecture/packages.md)
- [x] Initiative dashboard page (#74) — [CLI & Web](../../architecture/interfaces.md)
- [x] Initiative cost views in dashboard (#75) — [Observability](../../architecture/observability.md)

**Delivers:** Agents that take initiative; second domain (templates only) proves platform generality. Connector implementation (Linear, Notion, or Jira) deferred to a future phase.
