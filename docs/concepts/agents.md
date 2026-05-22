# Agents

## What an agent is

An **agent** owns a mailbox, an execution config, and an optional role definition.
It is the atomic unit of cognitive work in Spring Voyage: every message that
needs reasoning is ultimately delivered to an agent-shaped runtime invocation.

A **unit** is an agent that has children. A **leaf agent** is an agent with no
children. The dispatch path is the same for both, and both runtimes receive the
same platform MCP tools — the child list does not gate the tool surface. See [Units](units.md) for the
unit-specific layer, [Units vs agents](units-vs-agents.md) for the quick
reference on what features apply to both vs only one,
[ADR-0053](../decisions/0053-units-are-agents-and-one-way-delivery.md) for the
unit-as-agent and one-way-delivery decision,
[ADR-0038](../decisions/0038-agent-runtime-and-model-provider-split.md)
for the runtime/model config split,
[ADR-0046](../decisions/0046-unified-members-grammar.md) for the unified
package-YAML `members:` grammar that declares agents alongside sub-units and
humans, and [Agent runtime](../architecture/agent-runtime.md) for launcher
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
assembles the prompt, resolves credentials, mints a per-turn MCP session token,
and launches the selected runtime through `IAgentRuntimeLauncher`.

The launcher owns runtime-specific setup: workspace files, environment
variables, MCP wiring, and native tool attachment. The runtime then answers the
message directly or delivers a message to another addressable target to
coordinate work. See [Agent runtime](../architecture/agent-runtime.md) for the
launch path.

## Leaf agent vs. unit

A leaf agent has no children. A unit is an agent that has children. Both run
through the same runtime path and both receive the same platform MCP tools —
including the `sv.messaging.*` delivery tools. The child list does not gate the
tool surface; the runtime's instructions decide whether to answer directly or
deliver work to a member.

## Platform MCP tools

A runtime reaches the platform through one MCP server, with tools named
`sv.<area>.<verb>` ([ADR-0054](../decisions/0054-one-mcp-server-one-execution-host.md)).
The message-delivery surface is exactly two tools:

| Tool | Description |
| --- | --- |
| `sv.messaging.send` | One-way delivery of a message to a single addressable target. Returns a delivery acknowledgement — the message reached the recipient's mailbox — never the recipient's reply. |
| `sv.messaging.multicast` | One-way delivery to multiple targets, addressed explicitly or by a directory-relationship `scope` (`unit-members`, `siblings`). |

The platform delivers messages; it does not orchestrate. There is no
`delegate_to` / `fanout_to` tool — "delegation" is message *content* the
recipient's runtime interprets, not a distinct platform tool. A runtime
delegates by sending a message via `sv.messaging.send` whose content says so.

Discovery, inspection, memory, and runtime-status sit on the other areas —
`sv.directory.*` (`list_members`, `get_member`, `get_status`, `get_siblings`,
`get_parents`, `get_self`), `sv.memory.*`, `sv.expertise.*`, and `sv.runtime.*`.
Recording a routing decision on the activity stream is an optional, explicit
`sv.runtime.report_decision` call; a plain `sv.messaging.*` delivery records a
`MessageSent` activity and nothing more. The full catalogue is in
[Architecture: Messaging](../architecture/messaging.md#the-platform-mcp-tool-surface)
and [Tools](tools.md).

The launcher attaches the platform MCP surface unconditionally for every
`agent:` and `unit:` runtime; membership is not a gate. Unit operators and
runtime authors do not configure a separate routing layer.

## Inheritance

Inheritance is validated when agents are created, updated, assigned, or
unassigned. A single-parent agent may inherit any empty execution field from
its parent unit. A top-level agent inherits empty fields from tenant defaults.

When an agent has multiple parent units, each inherited field must resolve to
the same value from every parent. If parent configs diverge, the API rejects
the write with HTTP 422 and `MultiParentInheritanceConflict`; the operator must
set an explicit config value or change the parent set before the agent can
inherit safely.
