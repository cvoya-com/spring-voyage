# Orchestration

> **This page is retired.** Orchestration in Spring Voyage is the runtime's
> behaviour, not platform configuration.

The platform surfaces five orchestration tools to agent runtimes:
`list_children`, `inspect_child`, `delegate_to_child`, `fanout_to_children`,
`query_child_status`. The runtime (e.g. Claude, Codex) decides when and how to
use them.

See [docs/concepts/agents.md](../concepts/agents.md) for the current model.
