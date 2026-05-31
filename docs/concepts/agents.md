# Agents

## What an agent is

An **agent** owns a mailbox, an execution config, and an optional role definition.
It is the atomic unit of cognitive work in Spring Voyage: every message that
needs reasoning is ultimately delivered to an agent-shaped runtime invocation.

A **unit** is an agent that has children. A **leaf agent** is an agent with no
children. The dispatch path is the same for both, and both runtimes receive the
same platform MCP tools — the child list does not gate the tool surface. See [Units](units.md) for the
unit-specific layer and [Units vs agents](units-vs-agents.md) for the quick
reference on what features apply to both vs only one.

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
and launches the selected runtime.

The launcher owns runtime-specific setup: workspace files, environment
variables, MCP wiring, and native tool attachment. The runtime then answers the
message directly or delivers a message to another addressable target to
coordinate work.

## Leaf agent vs. unit

A leaf agent has no children. A unit is an agent that has children. Both run
through the same runtime path and both receive the same platform MCP tools —
including the `sv.messaging.*` delivery tools. The child list does not gate the
tool surface; the runtime's instructions decide whether to answer directly or
deliver work to a member.

## Platform MCP tools

A runtime reaches the platform through one MCP server, with tools named
`sv.<area>.<verb>`. The message-delivery surface is exactly two tools:

| Tool | Description |
| --- | --- |
| `sv.messaging.send` | Deliver one message to every recipient on the SINGLE SHARED thread for `{caller} ∪ recipients`. Recipients see the others in the inbound envelope's `to`. Returns a per-recipient delivery acknowledgement — never the recipient's reply. |
| `sv.messaging.multicast` | Deliver the same message to every recipient on its OWN INDEPENDENT 1-1 thread `{caller, recipient_i}`. Each recipient sees only itself in its envelope. |

Both tools take the same input shape — either an explicit `recipients` list or
a relationship `scope` (`unit-members`, `siblings`) — and differ in thread
identity, not input shape. The calling participant is auto-included in every
participant set; the runtime does not list itself in `recipients`. The runtime
never names a `thread_id` — the platform derives it from the participant set. Connectors
can stamp inbound messages as the sender but cannot receive: passing a
connector address as a recipient returns an error.

The platform delivers messages; it does not orchestrate. There is no
`delegate_to` / `fanout_to` tool — "delegation" is message *content* the
recipient's runtime interprets, not a distinct platform tool. A runtime
delegates by sending a message via `sv.messaging.send` whose content says so.

Discovery, inspection, memory, and runtime-status sit on the other areas —
`sv.directory.*` (`list_members`, `get_member`, `get_status`, `get_siblings`,
`get_parents`, `get_self`), `sv.memory.*` (private memory plus the
participant-set shared timeline tools), `sv.expertise.*`, and `sv.runtime.*`.
Recording a routing decision on the activity stream is an optional, explicit
`sv.runtime.report_decision` call; a plain `sv.messaging.*` delivery records a
`MessageSent` activity and nothing more. The full catalogue is in
[Tools](tools.md).

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
