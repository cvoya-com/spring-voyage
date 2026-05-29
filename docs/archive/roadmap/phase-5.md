# Phase 5: Unit Nesting + Directory + Boundaries

> **Historical planning record.** This describes planned work; for the current system see [docs/architecture/](../../architecture/README.md). Kept for context.

> **[Roadmap Index](README.md)** | _Historical snapshot — live progress in the [V2 milestone](https://github.com/cvoya-com/spring-voyage/milestone/1) and umbrella [#418](https://github.com/cvoya-com/spring-voyage/issues/418)._

Organizational structure beyond flat teams.

## Shipped

- [x] Recursive composition — units containing units (#98)
- [x] M:N agent-to-unit membership (#160, dispatch-time config #243)
- [x] Basic directory service (exists but not recursive aggregation)
- [x] Expertise directory aggregation — recursive composition to root (#412)
- [x] Unit boundary — opacity, projection, filtering, synthesis (#413)

## Remaining

- [ ] Hierarchy-aware permission checks (#414) — [Messaging](../../architecture/messaging.md), [Security](../../architecture/security.md)
- [ ] Proactive + Autonomous initiative levels (#415) — [Initiative](../../architecture/units-and-agents.md)
- [ ] Persistent cloning policy — independent clone evolution, recursive cloning (#416)

**Delivers:** Complex organizational structures with hierarchy-aware routing, full cloning spectrum.
