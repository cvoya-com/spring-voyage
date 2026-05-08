# Orchestration

> **This page is retired.** Orchestration in Spring Voyage is the runtime's
> behaviour, not platform configuration.

The platform surfaces five orchestration tools to agent runtimes:
`list_children`, `inspect_child`, `delegate_to_child`, `fanout_to_children`,
`query_child_status`. The runtime (e.g. Claude, Codex) decides when and how to
use them.

Legacy unit manifests that still declare a root `orchestration:` block are
rejected at parse time with `LegacyUnitOrchestrationField`; configure the
unit's runtime through `ai:` / `execution:` instead.

See [docs/concepts/agents.md](../concepts/agents.md) for the current model.
