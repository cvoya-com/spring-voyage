# Agents

## What an agent is

An **agent** owns a mailbox, an execution config, and an optional role definition.
It is the atomic unit of cognitive work in Spring Voyage: every message that
needs reasoning is ultimately delivered to an agent-shaped runtime invocation.

A **unit** is an agent that has children. A **leaf agent** is an agent with no
children. The dispatch path is the same for both; the child list only controls
whether orchestration tools are attached. See [Units](units.md) for the
unit-specific layer, [Units vs agents](units-vs-agents.md) for the quick
reference on what features apply to both vs only one,
[ADR-0039](../decisions/0039-units-are-agents.md) for the unit-as-agent
decision, [ADR-0038](../decisions/0038-agent-runtime-and-model-provider-split.md)
for the runtime/model config split,
[ADR-0046](../decisions/0046-unified-members-grammar.md) for the unified
package-YAML `members:` grammar that declares agents alongside sub-units and
humans, and [Agent Runtime](../architecture/agent-runtime.md) for launcher
details.

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

## The orchestration tools

The orchestration tool surface is closed for v0.1 to **two action verbs**:

| Tool | Description |
| --- | --- |
| `delegate_to` | Routes the current message thread to a single addressable target and waits for the response within the turn budget. |
| `fanout_to` | Routes the current work to multiple addressable targets in parallel and collects one result per target. |

Discovery, inspection, and runtime-status queries live on the `sv.*` directory
tool surface, not on the orchestration surface — `sv.list_members`,
`sv.get_member`, `sv.get_status`, plus `sv.get_siblings` / `sv.get_parents` /
`sv.get_self` for broader directory navigation.

The launcher attaches both action verbs unconditionally for every `agent://`
and `unit://` runtime; membership is not a gate. Unit operators and runtime
authors do not configure a separate routing layer for them.

## Orchestration decisions

When a runtime calls a delegation tool, the platform publishes an
`ActivityEvent` with `EventType = DecisionMade` and an
`OrchestrationDecision` payload:

| Field | Type | Description |
| --- | --- | --- |
| `Kind` | `Delegate` \| `Fanout` | What the runtime decided to do. |
| `Status` | `Accepted` \| `Routed` \| `Failed` | Outcome of the delegation attempt. |
| `Targets` | `Address[]` | Child unit(s) the work was routed to. |
| `ResultMessageIds` | `Guid[]` | IDs of the child responses. |
| `Reason` | `string?` | Runtime-supplied explanation (failure reason or routing rationale). |

Delegation events use `Kind = Delegate` for `delegate_to` and
`Kind = Fanout` for `fanout_to`. `Status` is `Routed` when the
platform routed the target message and `Failed` when the tool call could not
complete. `Targets` contains the target addresses, `ResultMessageIds` contains
any response message ids, and `Reason` is optional runtime-supplied text. The
`sv.*` directory tools are read-only probes and do not emit
`OrchestrationDecision` events.

See [ADR-0039 § 4](../decisions/0039-units-are-agents.md#4-orchestration-decisions-are-first-class-evidence)
for the full record definition.

## Inheritance

Inheritance is validated when agents are created, updated, assigned, or
unassigned. A single-parent agent may inherit any empty execution field from
its parent unit. A top-level agent inherits empty fields from tenant defaults.

When an agent has multiple parent units, each inherited field must resolve to
the same value from every parent. If parent configs diverge, the API rejects
the write with HTTP 422 and `MultiParentInheritanceConflict`; the operator must
set an explicit config value or change the parent set before the agent can
inherit safely.
