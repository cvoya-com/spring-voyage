# Messaging and Interaction

This guide covers how to send messages to agents, units, and humans on the Spring Voyage platform, how conversations form and evolve, and how to pick the right address for the job.

For the internals — mailbox partitioning, cancellation semantics, pub/sub streaming — see [Messaging architecture](../../architecture/messaging.md).

## Concepts at a glance

Spring Voyage models every addressable participant — a named agent, a composite unit, a human operator, a connector — as an **actor** with a stable `Guid` identity. A **message** travels from a `From` address to a `To` address, optionally carrying a **thread id** so the receiving actor knows whether to treat the incoming text as the start of new work or as a follow-up to work already in flight.

An address is the pair `(scheme, Guid)` and renders on the wire as `scheme:<32-hex-no-dash>` — for example `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7`. There is no path-shaped address; identity is the `Guid`. The platform does not inspect message content to decide routing; it reads the `To` scheme and id, looks the actor up in the directory, and delivers the message once. The actor on the receiving end — an `AgentActor`, `UnitActor`, `HumanActor`, or connector — is responsible for turning the payload into work.

See [Identifiers](../../architecture/identifiers.md) for the full identifier model (wire forms, parser rules, OSS default tenant id, manifest grammar, CLI search-with-context), and [Messaging architecture — Addressing](../../architecture/messaging.md#addressing) for the routing surface.

## Sending a message from the CLI

The CLI exposes a single command for sending messages:

```
spring message send <address> "<text>" [--thread <id>]
```

The address is `scheme:<32-hex-no-dash>` for one of `agent`, `unit`, `human`, or `connector`. The text is wrapped in a domain message and delivered to the destination actor. A new thread is started when `--thread` is omitted; passing an existing thread id appends the message to that thread.

Every `spring message send` call prints the generated message id and thread id so scripts can correlate follow-ups.

### Example: human talks to an agent

Start by resolving the agent's `Guid` — `spring agent show <name>` accepts a display-name search and prints the canonical id (and walks the operator through disambiguation when more than one agent matches):

```bash
spring agent show ada --unit engineering-team
# → ada   Guid: 8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7  …
```

Then address the agent by id:

```bash
spring message send agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7 "Review the README and suggest improvements"
```

The CLI resolves the address via the platform directory, hands the domain message to the agent actor, and prints the generated message id. The agent picks it up on its next turn and starts working.

### Example: address a whole unit

When the sender does not know (or does not want to pick) which member should handle the work, target the unit itself and let its orchestration strategy decide:

```bash
spring message send unit:dd55c4ea8d725e43a9df88d07af02b69 "Implement the login feature described in issue #15"
```

The unit actor receives the message, applies boundary filtering, and dispatches to a member according to the orchestration strategy configured for that unit. Responses flow back through the same thread.

### Example: multicast

A multicast send dispatches a single domain message to every actor that matches a routing pattern (for example, all members of a unit advertising a given role). Multicast resolution expands the pattern into a set of addresses, each routed individually; responses are aggregated and returned to the sender.

## Conversations

A conversation is the platform's unit of correlated work. Every message carries an optional `ConversationId`; the receiving actor uses it to decide whether the message starts a new piece of work or continues one already in progress.

- **Creation.** Sending a message without `--conversation` starts a new conversation. The server assigns a fresh id, creates a new conversation channel on the receiving actor, and returns the id to the sender.
- **Continuation.** Sending additional messages with the same `--conversation <id>` appends them to the active channel. For the conversation that is currently ACTIVE on the actor, follow-ups are delivered at the next checkpoint so the agent can incorporate them without losing its current train of thought. For PENDING conversations the new message accumulates in the channel and is picked up when the conversation becomes active.
- **Conclusion.** A conversation normally ends when the agent emits a `Completed` event for its work; the channel is released, any result payload is published to observers, and the next pending conversation is promoted to ACTIVE.
- **Operator close (#1038).** When a dispatch hangs, fails, or simply needs to be abandoned, the operator can close the conversation explicitly:
  - CLI: `spring conversation close <id> [--reason <text>]`
  - HTTP: `POST /api/v1/conversations/{id}/close`

  The platform cancels any in-flight dispatch, removes the active-conversation pointer from each participating agent, emits a `ThreadClosed` activity event (correlated to the conversation id), and promotes the next pending conversation. Closing an unknown id is a no-op so the call is safe to retry.
- **Auto-close on dispatch failure (#1036).** When the dispatcher returns a non-zero `ExitCode` (e.g. container exit code 125 because the runtime image was missing), the agent now surfaces the failure rather than silently swallowing it: an `ErrorOccurred` event with the exit code + first stderr line is appended to the conversation, the failure response is still routed back to the original sender, and the conversation is cleared off the agent's active slot via the same path the explicit-close API uses. The agent unblocks and the next pending conversation is promoted automatically.

See [Messaging architecture — Partitioned Mailbox with Priority Processing](../../architecture/messaging.md#design-partitioned-mailbox-with-priority-processing) for the full lifecycle, including conversation suspension and multi-conversation scheduling.

### Replies, threading, and multi-turn responses

There is no separate `reply` verb, and two equivalent threading paths exist for follow-ups:

- **`spring message send <address> "<text>" --conversation <id>`** — reuses the generic send verb. Prefer this when the call site already has the address.
- **`spring conversation send --conversation <id> <address> "<text>"`** — the same effect, but reads as "post into conversation X". Prefer this for scripts that iterate over conversations and want the surface to match `spring conversation list` / `show`.

Both call the same `POST /api/v1/conversations/{id}/messages` endpoint under the hood, which in turn routes through the `IMessageRouter` with the conversation id stamped on the outbound message. The agent's own responses travel back on the same conversation channel: for hosted (in-process LLM) agents, each LLM turn produces one or more tokens and eventually a completion event; for delegated (container-based) agents, responses stream as activity events while the container runs.

To watch the reply traffic in real time, use the activity viewer:

```bash
spring activity list --source "agent:ada" --limit 20
```

This surfaces the activity events — message received, token deltas, checkpoints, completion — emitted on the shared activity stream. The web portal shows the same events in the unit and agent detail pages.

### Reading a conversation thread

Scripted review — or "what happened on conversation X?" in a terminal — uses the `spring conversation` verb family:

```bash
spring conversation list                        # most recent 50 conversations
spring conversation list --status active        # only open threads
spring conversation list --unit engineering-team
spring conversation list --agent ada
spring conversation show <conversation-id>      # full thread: summary + ordered events
```

`list` renders one row per conversation with id, status, origin, participants, event count, last activity, and the originating summary. `show` prints the conversation header (participants, origin, created / last activity) followed by the full event timeline. Both commands accept `--output json` for downstream tooling.

### Inbox: things awaiting a human

When agents hand work back to a human — approvals, clarifications, go / no-go decisions — the inbox is the corresponding surface. It lists conversations whose most recent event was a `MessageReceived` addressed to the current human and where the human has not yet replied:

```bash
spring inbox list                          # awaiting-me queue
spring inbox show <conversation-id>        # open the thread in context
spring inbox respond <conversation-id> "Approved — ship it."
```

`respond` is a thin wrapper over `spring conversation send --conversation <id>`: it resolves the pending ask's sender automatically, so the common case (reply to whoever asked) needs no address. Pass `--to <address>` when you want to redirect the reply to a different participant.

See [Observing Activity](observing.md#conversations-and-inbox) for more examples.

## Addressing scheme — when to use each

| Scheme        | Shape                          | When to use                                                                                          |
| ------------- | ------------------------------ | ---------------------------------------------------------------------------------------------------- |
| `agent`       | `agent:<32-hex-no-dash>`       | You know exactly which member should handle the work.                                                |
| `unit`        | `unit:<32-hex-no-dash>`        | You want the unit's orchestration strategy to pick a member (or the message is for the unit itself). |
| `human`       | `human:<32-hex-no-dash>`       | You want to route a message to a human participant (notifications, approvals, escalations).         |
| `connector`   | `connector:<32-hex-no-dash>`   | You want to invoke a connector (e.g. a GitHub connector) as if it were a peer actor.                |

Address parsers are lenient: the dashed Guid form (`agent:8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7`) is accepted everywhere, but the canonical render uses the no-dash form. `display_name` is presentation-only — never an addressable handle. Use `spring agent show <name>` / `spring unit show <name>` to look up the canonical id when you only know a name.

See [Identifiers](../../architecture/identifiers.md) for the wire-form rules and [Messaging architecture — Addressing](../../architecture/messaging.md#addressing) for the resolution algorithm and permission model.

## Cross-unit messaging

A sender in one unit can target an actor in a different unit by supplying the actor's `Guid`. The router resolves the destination in a single directory lookup and enforces the sender's permissions at each membership-graph edge from the addressed actor toward the tenant root — cross-unit delivery is one synchronous permission check per edge, not a forwarded hop through each unit's actor.

```bash
# Ada in engineering-team asks Kay (in research-team) for a design review.
spring message send agent:f47ac10b58cc4372a5670e02b2c3d479 \
  "Please review the API design in PR #73 when you have a moment."
```

If the sender lacks permission to reach the addressed agent (the receiving unit denies deep access, or the addressed member is private to its unit), the send returns a permission-denied error and the message never reaches the destination actor.

## Tips

- **Let the unit route when in doubt.** Addressing the unit (`unit:<id>`) and letting the orchestration strategy pick a member is usually the right default for cross-team requests. Pin to a specific `agent:<id>` only when the work genuinely needs that specific agent.
- **Hold on to thread ids.** Pass the same `--thread <id>` on follow-ups so the agent's mailbox threads your messages together. Without it, each send creates a fresh thread — noisier and harder to follow.
- **Multicast is an aggregator, not a fan-out trigger.** A multicast send waits for every matching actor to respond before returning an aggregate payload to the sender. Use it to broadcast announcements; avoid it for long-running work where you want the first responder to win.
- **The web portal shows the same traffic.** The portal's unit and agent pages display activity events (messages, checkpoints, completions) for any work you drive from the CLI. CLI and portal stay in lock-step — either surface is a valid operator entry point.

## See it in action

Two `pool: fast` CLI scenarios exercise the messaging plumbing without needing an LLM backend:

- [`messaging/agent-domain-message.sh`](../../../tests/cli-scenarios/scenarios/messaging/agent-domain-message.sh) — sends a Domain message to an agent and verifies the `MessageReceived` activity event lands. Proves the router → actor → activity-bus path end-to-end.
- [`messaging/conversation-lifecycle.sh`](../../../tests/cli-scenarios/scenarios/messaging/conversation-lifecycle.sh) — starts a fresh conversation on an idle agent and verifies the three lifecycle events fire in order: `MessageReceived` → `ThreadStarted` → `StateChanged (Idle→Active)`.

Scenario [`messaging/message-human-to-agent.sh`](../../../tests/cli-scenarios/scenarios/messaging/message-human-to-agent.sh) (`pool: llm`, requires Ollama) drives the full human-to-agent round-trip through `spring message send`. See [Runnable Examples](examples.md) for the full catalogue.
