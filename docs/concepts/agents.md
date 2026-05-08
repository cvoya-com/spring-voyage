# Agents

An **agent** is a named, addressable actor with a mailbox and an execution
configuration. It can receive messages, run a configured runtime, and decide
how to respond. A unit is also an agent: it has the same mailbox and execution
shape, with child membership added on top. A leaf agent can live in zero or
more units, so parent units can supply inherited defaults without changing the
agent's identity.

See [Units](units.md) for the unit-specific layer, [ADR-0039](../decisions/0039-units-are-agents.md)
for the unit-as-agent decision, and [ADR-0038](../decisions/0038-agent-runtime-and-model-provider-split.md)
for the runtime/model configuration shape.

## Agent identity

Every agent has one stable `Guid` identity and one presentation-only
`display_name`. The canonical address form is `agent:<32-hex-no-dash>`, for
example `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7`. Display names can change and
are never used as routing keys. See [Identifiers](../architecture/identifiers.md)
for the full address grammar.

Agents also carry role, capability, instruction, expertise, and activation
metadata. Those fields help the runtime understand what the agent is for; they
do not replace the `Guid` address.

## Agent lifecycle

An agent's lifecycle is: **define -> create -> activate -> run -> deactivate ->
delete**.

1. **Define** - describe the agent in YAML, JSON, the CLI, or the portal.
2. **Create** - the platform registers the agent and assigns its `Guid`.
3. **Activate** - the Dapr virtual actor wakes on first message.
4. **Run** - the runtime processes messages, collaborates, and records activity.
5. **Deactivate** - the actor goes idle after a timeout; state is preserved.
6. **Delete** - the agent is soft-deleted; history remains available for audit.

## Mailbox model

Each agent has a mailbox with three logical channel types:

| Channel | Purpose |
| --- | --- |
| Control | Cancellation, status query, health check, and policy update messages. These are handled ahead of ordinary work. |
| Conversation | Per-thread work messages. The active thread runs now; new work queues until the runtime can checkpoint or finish. |
| Observation | Batched events from subscriptions, timers, observed agents, and external systems. Initiative processing reads this stream. |

The mailbox belongs to the agent primitive, so units and leaf agents share this
same message intake model.

## Execution config

The execution config tells the platform how to invoke the agent:

- `ai.runtime` names the agent runtime, such as `claude-code`, `codex`,
  `gemini`, or `spring-voyage`.
- `ai.model.provider` and `ai.model.id` identify the model. The provider is
  intrinsic to the model and is the credential boundary.
- `execution.image` names the container image to run when the selected runtime
  needs one.
- `execution.hosting` selects ephemeral or persistent hosting.

Each field can be left empty. Empty fields inherit from the parent unit's
resolved config; top-level agents inherit from tenant defaults. Multi-parent
agents inherit a field only when every parent resolves the same value for that
field.

## Runtime invocation

When a message is due, the runtime invocation path resolves the effective
execution config, assembles the prompt, resolves credentials, and calls the
`IAgentRuntimeLauncher` for the selected runtime. The launcher's job is to
materialise the runtime-specific invocation: workspace files, environment
variables, callback credentials, and any orchestration tools available for this
agent.

## Leaf vs. with children

A leaf agent has no children. Its runtime is invoked directly and no
orchestration tools are attached.

An agent with children is still invoked directly. The difference is that the
launcher attaches the closed orchestration-tool surface, and the runtime decides
whether to answer itself or coordinate with a child.

## The five orchestration tools

The platform exposes five tools to runtimes for agents with children:

| Tool | Purpose |
| --- | --- |
| `list_children` | List direct children with address, display name, kind, and resolved execution metadata. |
| `inspect_child` | Return one child's role, description, expertise, and current status. |
| `delegate_to_child` | Send the in-flight work to one direct child and await the child's response. |
| `fanout_to_children` | Send the work to multiple direct children in parallel and collect their results. |
| `query_child_status` | Read a cheap status summary for a direct child. |

These tools are not unit configuration. They are runtime capabilities attached
when the agent has children.

## OrchestrationDecision event

When the platform processes a delegation tool call, it emits an
`OrchestrationDecision` event. The event records the decision kind, the child
target or targets, status, and the payload/response evidence needed for audit.
Delegation and fan-out are the primary decision kinds; inspection and no-op are
reserved for explicit decision sequences.

## Inheritance

Inheritance is per field, not all-or-nothing. An agent can inherit `ai.runtime`
from its parent unit while setting its own `ai.model.id`, or inherit the model
while overriding `execution.image`. Leaving a field empty means "inherit this
field"; setting it makes the agent's value authoritative for that field.

## Cloning policies

When an agent is busy and new work arrives, the platform can create clones to
handle concurrent work. The policy controls what happens to state and learning:

| Policy | Behavior |
| --- | --- |
| `none` | Singleton. Work queues when the agent is busy. |
| `ephemeral-no-memory` | Clone handles one thread of work, then disappears without writing learning back. |
| `ephemeral-with-memory` | Clone handles one thread, sends learnings back, then disappears. |
| `persistent` | Clone remains as an independent agent with its own state. |

Detached clones become peers in the same unit. Attached clones make the parent
look like one agent from outside while using children internally.

## Platform tools

Agents interact with Spring Voyage through platform tools exposed to the
runtime. Core tools include:

| Tool | Purpose |
| --- | --- |
| `checkMessages` | Retrieve pending messages for the active thread. |
| `discoverPeers` | Query the directory for agents or units with matching expertise. |
| `requestHelp` | Ask another agent or unit for assistance. |
| `store` | Persist a memory artifact to the agent's `AgentMemory`. |
| `recall` | Read visible entries from `AgentMemory`. |
| `checkpoint` | Save progress so messages and runtime state can be recovered. |
| `reportStatus` | Update the activity stream with current progress. |
| `escalate` | Raise a question, blocker, or approval need to a human or unit. |

Connectors and installed skill bundles can add additional tools, but they build
on the same agent runtime invocation model.
