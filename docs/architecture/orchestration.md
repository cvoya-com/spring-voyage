# Orchestration

> **This document is retired.** Orchestration in Spring Voyage is runtime behaviour, not platform configuration. The platform does not model an orchestration-strategy taxonomy or a `unit.orchestration:` block.
>
> See [ADR-0039 § 3](../decisions/0039-units-are-agents.md#3-children-are-exposed-as-orchestration-tools-to-the-runtime) for the orchestration-tool surface (`list_children`, `inspect_child`, `delegate_to_child`, `fanout_to_children`, `query_child_status`) and [ADR-0039 § 4](../decisions/0039-units-are-agents.md#4-orchestration-decisions-are-first-class-evidence) for the `OrchestrationDecision` event shape. The current narrative lives in [Units & Agents](units.md) and [Agents](agents.md).
