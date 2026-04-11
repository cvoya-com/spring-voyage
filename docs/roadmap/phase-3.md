# Phase 3: Initiative + Product Management Domain

> **[Roadmap Index](README.md)** | **Status: Not started**

Agents start taking initiative. A product management domain package (templates only, no connector) proves the platform is domain-agnostic.

## Deliverables

- [ ] Initiative types, policy model, and decision enums (#62) — [Initiative](../architecture/initiative.md)
- [ ] ICognitionProvider interface for tiered screening (#63) — [Initiative](../architecture/initiative.md)
- [ ] IInitiativeEngine interface (#64) — [Initiative](../architecture/initiative.md)
- [ ] ICancellationManager interface (#65) — [Messaging](../architecture/messaging.md)
- [ ] InitiativeEngine implementation (#66) — [Initiative](../architecture/initiative.md)
- [ ] Tier 1 CognitionProvider — Ollama (#67) — [Initiative](../architecture/initiative.md)
- [ ] Tier 2 CognitionProvider — primary LLM (#68) — [Initiative](../architecture/initiative.md)
- [ ] AgentActor initiative integration (#69) — [Initiative](../architecture/initiative.md)
- [ ] CancellationManager + execution propagation (#70) — [Messaging](../architecture/messaging.md)
- [ ] Initiative API endpoints (#71) — [Initiative](../architecture/initiative.md)
- [ ] DI registration for initiative services (#72) — [Initiative](../architecture/initiative.md)
- [ ] Product management domain package — agent/unit/skill templates, no connector (#73) — [Packages](../architecture/packages.md)
- [ ] Initiative dashboard page (#74) — [CLI & Web](../architecture/cli-and-web.md)
- [ ] Initiative cost views in dashboard (#75) — [Observability](../architecture/observability.md)

**Delivers:** Agents that take initiative; second domain (templates only) proves platform generality. Connector implementation (Linear, Notion, or Jira) deferred to a future phase.
