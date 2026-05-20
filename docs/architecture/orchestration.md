# Orchestration

> **This document is retired.** Orchestration in Spring Voyage is runtime behaviour, not platform configuration. The platform does not model an orchestration-strategy taxonomy or a `unit.orchestration:` block.
>
> See [ADR-0039 § 3](../decisions/0039-units-are-agents.md#3-children-are-exposed-as-orchestration-tools-to-the-runtime) for the orchestration-tool surface — the action verbs `delegate_to` and `fanout_to` (discovery / inspection / status queries live on the `sv.*` directory tool surface) — and [ADR-0039 § 4](../decisions/0039-units-are-agents.md#4-orchestration-decisions-are-first-class-evidence) for the `OrchestrationDecision` event shape. The current narrative lives in [Units & Agents](units.md) and [Agents](agents.md).
