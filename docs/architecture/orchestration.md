# Orchestration

> **This document is retired.** Orchestration in Spring Voyage is runtime behaviour, not platform configuration. The platform does not model an orchestration-strategy taxonomy or a `unit.orchestration:` block.
>
> The platform has no orchestration tools. A runtime delivers messages with the `sv.messaging.send` / `sv.messaging.broadcast` tools — see [ADR-0050](../decisions/0050-platform-mcp-tool-surface.md) and [Platform MCP Tools](platform-mcp-tools.md). "Delegation" is message *content* the recipient's runtime interprets; recording a routing decision is an optional `sv.runtime.report_decision` call. The current narrative lives in [Units & Agents](units.md) and [Agents](agents.md).
