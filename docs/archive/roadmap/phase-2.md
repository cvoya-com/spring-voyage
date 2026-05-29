# Phase 2: Observability + Multi-Human

> **Historical planning record.** This describes planned work; for the current system see [docs/architecture/](../../architecture/README.md). Kept for context.

> **[Roadmap Index](README.md)** | _Historical snapshot — live progress in the [V2 milestone](https://github.com/cvoya-com/spring-voyage/milestone/1) and umbrella [#418](https://github.com/cvoya-com/spring-voyage/issues/418)._

Real-time visibility into what agents are doing, and support for multiple human participants.

## Deliverables

- [x] Enrich ActivityEvent model + Rx.NET pipeline (models + schema; #1) — [Observability](../../architecture/observability.md)
- [x] Streaming event types + Dapr pub/sub transport (#2) — [Messaging](../../architecture/messaging.md)
- [x] Basic cost tracking service + aggregation (schema; #3) — [Observability](../../architecture/observability.md)
- [x] Multi-human RBAC with unit-scoped permissions (#4) — [Security](../../architecture/security.md)
- [x] Clone state model + ephemeral lifecycle (model; #5) — [Units & Agents](../../architecture/units-and-agents.md)
- [x] Clone API endpoints + cost attribution (model; #6) — [Units & Agents](../../architecture/units-and-agents.md)
- [x] Real-time SSE endpoint + activity query API (model; #7) — [Observability](../../architecture/observability.md)
- [x] Wire Rx.NET reactive pipeline end-to-end (#44) — [Observability](../../architecture/observability.md)
- [x] Implement cost tracking aggregation service + API endpoints (#41) — [Observability](../../architecture/observability.md)
- [x] Implement agent cloning lifecycle workflow + clone API (#43) — [Units & Agents](../../architecture/units-and-agents.md)
- [x] Implement SSE activity stream endpoint + activity query API (#42) — [Observability](../../architecture/observability.md)
- [x] React/Next.js web dashboard (#8) — [CLI & Web](../../architecture/interfaces.md)

## Follow-up Enhancements (tracked in Phase 4)

These extend Phase 2 deliverables and are tracked under [Phase 4](phase-4.md):

- [x] Complete Rx.NET activity pipeline end-to-end (#391) — full observable graph wiring
- [ ] Dashboard: drill-down views for units, agents, and conversations (#392)
- [ ] Dashboard: multi-human RBAC management UI (#393)
- [ ] Dashboard: cost rollup and per-agent attribution (#394)

**Delivers:** Real-time observation of agent work, multi-human participation, elastic agent scaling.
