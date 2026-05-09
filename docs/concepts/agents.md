# Agents

## What an agent is

An **agent** owns a mailbox, an execution config, and an optional role definition.
It is the atomic unit of cognitive work in Spring Voyage: every message that
needs reasoning is ultimately delivered to an agent-shaped runtime invocation.

A **unit** is an agent that has children. A **leaf agent** is an agent with no
children. The dispatch path is the same for both; the child list only controls
whether orchestration tools are attached. See [Units](units.md) for the
unit-specific layer, [ADR-0039](../decisions/0039-units-are-agents.md) for the
unit-as-agent decision, [ADR-0038](../decisions/0038-agent-runtime-and-model-provider-split.md)
for the runtime/model config split, and
[Agent Runtime](../architecture/agent-runtime.md) for launcher details.

## Mailbox

An agent receives messages through Dapr pub/sub and actor routing. The platform
resolves the target address, activates the corresponding Dapr actor, and hands
the message to the actor's mailbox. Control messages such as cancellation,
status, health, and policy updates are handled ahead of ordinary work; domain
messages enter the runtime invocation path.

## Execution config

The execution config tells the platform how to run the agent:

| Field | Meaning |
| --- | --- |
| `runtime` | Agent runtime id from the runtime catalogue, such as `spring-voyage`, `claude-code`, `codex`, or `gemini`. |
| `model` | Structured model selection `{ provider, id }`; the provider is intrinsic to the model. |
| `image` | Container image used when the selected runtime needs one. |
| `hosting` | Runtime hosting mode, currently ephemeral or persistent. |

Each field can be left empty. Empty means "inherit": from the parent unit when
there is one, or from the tenant default when the agent is top-level. Explicit
agent values override inherited values per field.

## Runtime invocation

At dispatch time the platform resolves the effective execution config,
assembles the prompt, resolves credentials, asks
`IOrchestrationToolProvider` for the tools available to this agent in the
current thread, and launches the selected runtime through
`IAgentRuntimeLauncher`.

The launcher owns runtime-specific setup: workspace files, environment
variables, callback credentials, MCP wiring, and native tool attachment. The
runtime then answers the message directly or, when orchestration tools are
present, may coordinate with children.

## Leaf agent vs. unit

A leaf agent has no children. It uses the same runtime path, but the
orchestration tool set is empty.

When an agent has children, it is a unit. The launcher attaches the five
orchestration tools to the runtime context. The runtime may call those tools to
delegate or fan out work, and the platform records each delegation as an
`OrchestrationDecision` activity event.

## Orchestration tools

The tool surface is closed for v0.1:

| Tool | Description |
| --- | --- |
| `list_children` | Returns the unit's current direct children with addresses, display names, kinds, and resolved execution metadata. It has no routing side effect. |
| `inspect_child` | Returns one child's role, description, declared expertise, and current status. Use it when the runtime needs more context than the child list provides. |
| `delegate_to_child` | Routes the current message thread to exactly one direct child and waits for that child's response within the turn budget. |
| `fanout_to_children` | Routes the current work to multiple direct children in parallel and collects one result per target. |
| `query_child_status` | Returns the last-known execution status for a direct child's thread without a full inspection payload. |

The launcher attaches these tools automatically when children exist. Unit
operators and runtime authors do not configure a separate routing layer for
them.

## Orchestration decisions

When a runtime calls a delegation tool, the platform publishes an
`ActivityEvent` with `EventType = DecisionMade` and an
`OrchestrationDecision` payload:

```text
OrchestrationDecision {
  Kind,
  Status,
  Targets,
  ResultMessageIds,
  Reason
}
```

Delegation events use `Kind = Delegate` for `delegate_to_child` and
`Kind = Fanout` for `fanout_to_children`. `Status` is `Routed` when the
platform routed the child message and `Failed` when the tool call could not
complete. The domain enum also reserves `Inspect`, `NoOp`, and `Accepted` for
explicit decision sequences and accepted-but-not-yet-routed work. `Targets`
contains the child addresses, `ResultMessageIds` contains any child response
message ids, and `Reason` is optional runtime-supplied text.

## Inheritance

Inheritance is validated when agents are created, updated, assigned, or
unassigned. A single-parent agent may inherit any empty execution field from
its parent unit. A top-level agent inherits empty fields from tenant defaults.

When an agent has multiple parent units, each inherited field must resolve to
the same value from every parent. If parent configs diverge, the API rejects
the write with HTTP 422 and `MultiParentInheritanceConflict`; the operator must
set an explicit config value or change the parent set before the agent can
inherit safely.
