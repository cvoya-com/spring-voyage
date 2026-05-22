# Archived Decision Records

These ADRs are kept for their **reasoning history**, not as a description of the
current system. Every record here has been superseded — either by a later ADR
that reversed or narrowed its decision, or by a **re-baseline ADR** that restates
the current design directly so a reader no longer has to replay a supersession
chain.

An ADR is never deleted: the "why not the obvious alternative?" reasoning is the
point of the record, and that reasoning stays useful even after the decision
moves on. But a superseded ADR should not be read as current. If you want to
know how the system works *today*, start from [`../README.md`](../README.md) and
the [architecture docs](../../architecture/README.md).

> Relative links inside an archived ADR point at the decisions tree as it stood
> when the ADR was archived. They are frozen with the document; do not rely on
> them resolving.

## What's here and why

| # | Title | Superseded by |
|---|-------|---------------|
| 0001 | Web portal rendering strategy | [0005](../0005-portal-standalone-mode.md) — portal runs in Next.js `standalone` mode |
| 0007 | Label-routing match semantics | [0053](../0053-units-are-agents-and-one-way-delivery.md) — orchestration strategies removed |
| 0009 | GitHub label roundtrip via activity event | [0053](../0053-units-are-agents-and-one-way-delivery.md) — the roundtrip rewires to the connector binding |
| 0010 | Manifest orchestration-strategy selector | [0053](../0053-units-are-agents-and-one-way-delivery.md) — strategy selection removed; the runtime decides |
| 0018 | Three-channel partitioned mailbox | [0030](../0030-thread-model.md) — participant-set thread model |
| 0039 | Units are agents (with orchestration tools) | [0053](../0053-units-are-agents-and-one-way-delivery.md) — re-baseline (drops the dead `delegate_to` / `fanout_to` surface) |
| 0048 | Event-vs-request message semantics | [0053](../0053-units-are-agents-and-one-way-delivery.md) — re-baseline |
| 0049 | Message-delivery tool contract | [0053](../0053-units-are-agents-and-one-way-delivery.md) — re-baseline |
| 0050 | Platform MCP tool surface (`sv.<area>.<verb>`) | [0054](../0054-one-mcp-server-one-execution-host.md) — re-baseline |
| 0051 | Unified platform MCP auth model | [0054](../0054-one-mcp-server-one-execution-host.md) — re-baseline |
| 0052 | Execution host roles + single McpServer | [0054](../0054-one-mcp-server-one-execution-host.md) — re-baseline |

ADRs 0039 and 0048–0052 recorded the system as it evolved, in fast succession,
toward "the platform delivers messages, it does not orchestrate" and "one MCP
server, one execution host." Each was correct when written, but the live design
ended up spread across six records with amendment headers and strike-through
bodies. [ADR-0053](../0053-units-are-agents-and-one-way-delivery.md) and
[ADR-0054](../0054-one-mcp-server-one-execution-host.md) restate that design in
two clean records. The six are archived here for the reasoning — the rejected
alternatives in particular — that the re-baseline records summarise but do not
reproduce in full.
