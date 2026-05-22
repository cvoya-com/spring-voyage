# Message delivery

> **This document is a pointer.** Spring Voyage has no "orchestration" concept — the platform is a thin message-delivery substrate. Routing work to another agent or unit is runtime behaviour, not platform configuration: there is no orchestration-strategy taxonomy and no `unit.orchestration:` block.
>
> A runtime delivers messages with the `sv.messaging.send` / `sv.messaging.multicast` tools — see [ADR-0049](../decisions/0049-message-delivery-tool-contract.md), [ADR-0050](../decisions/0050-platform-mcp-tool-surface.md), and [Platform MCP Tools](platform-mcp-tools.md). "Delegation" is message *content* the recipient's runtime interprets; recording a routing decision is an optional `sv.runtime.report_decision` call. The full narrative lives in [Units & Agents](units.md) and [Agents](agents.md).
