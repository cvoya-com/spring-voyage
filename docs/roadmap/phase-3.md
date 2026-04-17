# Phase 3: Initiative + Product Management Domain

> **[Roadmap Index](README.md)** | **Status: Complete**

Agents start taking initiative. A product management domain package (templates only, no connector) proves the platform is domain-agnostic.

## Deliverables

- [x] Initiative types, policy model, and decision enums (#62) — [Initiative](../architecture/initiative.md)
- [x] ICognitionProvider interface for tiered screening (#63) — [Initiative](../architecture/initiative.md)
- [x] IInitiativeEngine interface (#64) — [Initiative](../architecture/initiative.md)
- [x] ICancellationManager interface (#65) — [Messaging](../architecture/messaging.md)
- [x] InitiativeEngine implementation (#66) — [Initiative](../architecture/initiative.md)
- [x] Tier 1 CognitionProvider — Ollama (#67) — [Initiative](../architecture/initiative.md)
- [x] Tier 2 CognitionProvider — primary LLM (#68) — [Initiative](../architecture/initiative.md)
- [x] AgentActor initiative integration (#69) — [Initiative](../architecture/initiative.md)
- [x] CancellationManager + execution propagation (#70) — [Messaging](../architecture/messaging.md)
- [x] Initiative API endpoints (#71) — [Initiative](../architecture/initiative.md)
- [x] DI registration for initiative services (#72) — [Initiative](../architecture/initiative.md)
- [x] Product management domain package — agent/unit/skill templates, no connector (#73) — [Packages](../architecture/packages.md)
- [x] Initiative dashboard page (#74) — [CLI & Web](../architecture/cli-and-web.md)
- [x] Initiative cost views in dashboard (#75) — [Observability](../architecture/observability.md)

**Delivers:** Agents that take initiative; second domain (templates only) proves platform generality. Connector implementation (Linear, Notion, or Jira) deferred to a future phase.
