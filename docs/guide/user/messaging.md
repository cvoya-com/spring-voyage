# Messaging and Interaction

This guide covers how to send messages to agents, units, and humans on the Spring Voyage platform, how conversations form and evolve, and how to pick the right address for the job.

For the internals — mailbox partitioning, cancellation semantics, pub/sub streaming — see [Messaging architecture](../../architecture/messaging.md).

## Concepts at a glance

Spring Voyage models every addressable participant — a named agent, a composite unit, a human operator, a connector — as an **actor** with a unique address. A **message** travels from a `From` address to a `To` address, optionally carrying a **conversation id** so the receiving actor knows whether to treat the incoming text as the start of new work or as a follow-up to work already in flight.

The platform does not inspect message content to decide routing; it reads the `To` scheme and path, looks the actor up in the directory, and delivers the message once. The actor on the receiving end — an `AgentActor`, `UnitActor`, `HumanActor`, or connector — is responsible for turning the payload into work.

See [Messaging architecture — Addressing](../../architecture/messaging.md#addressing) for the full routing model and [Messaging architecture — Agent Mailbox & Message Processing](../../architecture/messaging.md#agent-mailbox--message-processing) for how an agent actually processes what it receives.

## Sending a message from the CLI

The CLI exposes a single command for sending messages:

```
spring message send <address> "<text>" [--conversation <id>]
```

The address is any scheme Spring Voyage recognises: `agent://`, `unit://`, `human://`, `connector://`, or `role://` (multicast). The text is wrapped in a domain message and delivered to the destination actor. A new conversation is started when `--conversation` is omitted; passing an existing conversation id appends the message to that conversation.

Every `spring message send` call prints the generated message id so scripts can correlate follow-ups.

### Example: human talks to an agent

```bash
spring message send agent://engineering-team/ada "Review the README and suggest improvements"
```

The CLI resolves `agent://engineering-team/ada` via the platform directory, hands the domain message to the agent actor, and prints `Message sent to agent://engineering-team/ada. (id: <uuid>)`. The agent picks it up on its next turn, creates a conversation channel keyed off the message's conversation id, and starts working.

### Example: address a whole unit

When the sender does not know (or does not want to pick) which member should handle the work, target the unit itself and let its orchestration strategy decide:

```bash
spring message send unit://engineering-team "Implement the login feature described in issue #15"
```

The unit actor receives the message, applies boundary filtering, and dispatches to a member according to the orchestration strategy configured for that unit. Responses flow back through the same conversation id.

### Example: broadcast to a role

`role://` is a multicast scheme: every addressable entity that advertises the matching role receives a copy of the message, and responses are aggregated into a single reply payload when the router returns to the sender.

```bash
spring message send role://engineering-team/backend-engineer "New coding standards are in effect — please skim the doc."
```

The router fans out to every actor registered under the `backend-engineer` role inside `engineering-team` and collects their acknowledgements. If no matching actor is found the call fails with an address-not-found error rather than silently succeeding.

## Conversations

A conversation is the platform's unit of correlated work. Every message carries an optional `ConversationId`; the receiving actor uses it to decide whether the message starts a new piece of work or continues one already in progress.

- **Creation.** Sending a message without `--conversation` starts a new conversation. The server assigns a fresh id, creates a new conversation channel on the receiving actor, and returns the id to the sender.
- **Continuation.** Sending additional messages with the same `--conversation <id>` appends them to the active channel. For the conversation that is currently ACTIVE on the actor, follow-ups are delivered at the next checkpoint so the agent can incorporate them without losing its current train of thought. For PENDING conversations the new message accumulates in the channel and is picked up when the conversation becomes active.
- **Conclusion.** A conversation normally ends when the agent emits a `Completed` event for its work; the channel is released, any result payload is published to observers, and the next pending conversation is promoted to ACTIVE.
- **Operator close (#1038).** When a dispatch hangs, fails, or simply needs to be abandoned, the operator can close the conversation explicitly:
  - CLI: `spring conversation close <id> [--reason <text>]`
  - HTTP: `POST /api/v1/conversations/{id}/close`

  The platform cancels any in-flight dispatch, removes the active-conversation pointer from each participating agent, emits a `ConversationClosed` activity event (correlated to the conversation id), and promotes the next pending conversation. Closing an unknown id is a no-op so the call is safe to retry.
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

| Scheme        | Shape                                          | When to use                                                                                          |
| ------------- | ---------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| `agent://`    | `agent://<unit-path>/<name>` or `agent://@<uuid>` | You know exactly which member should handle the work (e.g. `agent://engineering-team/ada`).        |
| `unit://`     | `unit://<unit-path>`                           | You want the unit's orchestration strategy to pick a member (or the message is for the unit itself). |
| `human://`    | `human://<unit-path>/<identity>`              | You want to route a message to a human participant (notifications, approvals, escalations).         |
| `connector://` | `connector://<unit-path>/<type>`             | You want to invoke a connector (e.g. a GitHub connector) as if it were a peer actor.                |
| `role://`     | `role://<unit-path>/<role-name>`              | You want to multicast to every addressable entity with that role inside a unit.                     |

Two shapes are supported for the path portion:

- **Path addresses** — human-readable, reflect the organisation's unit hierarchy (`agent://engineering-team/ada`, `agent://engineering-team/backend-team/ada`). Resolved via the directory. Permission checks are applied along the unit path.
- **Direct addresses (`@<uuid>`)** — stable, independent of the agent's current unit (e.g. `agent://@f47ac10b-58cc-4372-a567-0e02b2c3d479`). Useful when scripts persist references and cannot tolerate hierarchy changes.

See [Messaging architecture — Addressing](../../architecture/messaging.md#addressing) for the resolution algorithm and permission model.

## Cross-unit messaging

A sender in one unit can target an actor in a different unit by supplying the full path. The router resolves the destination path in a single directory lookup and enforces the sender's permissions at each unit boundary along the way — cross-unit delivery is one synchronous permission check per boundary, not a forwarded hop through each unit's actor.

```bash
# Ada in engineering-team asks research-team for a design review.
spring message send agent://research-team/kay "Please review the API design in PR #73 when you have a moment."
```

If `engineering-team` does not have permission to reach `agent://research-team/kay` (the receiving unit denies deep access, or the addressed member is private to its unit), the send returns a permission-denied error and the message never reaches the destination actor.

See [Messaging architecture — Routing Mechanism](../../architecture/messaging.md#routing-mechanism) for the boundary-resolution semantics.

## Tips

- **Let the unit route when in doubt.** Addressing `unit://engineering-team` and letting the orchestration strategy pick a member is usually the right default for cross-team requests. Pin to a specific `agent://` address only when the work genuinely needs that specific agent.
- **Hold on to conversation ids.** Pass the same `--conversation <id>` on follow-ups so the agent's mailbox threads your messages together. Without it, each send creates a fresh pending conversation — noisier and harder to follow.
- **Multicast is an aggregator, not a fan-out trigger.** `role://` waits for every matching actor to respond before returning an aggregate payload to the sender. Use it to broadcast announcements; avoid it for long-running work where you want the first responder to win.
- **The web portal shows the same traffic.** The portal's unit and agent pages display activity events (messages, checkpoints, completions) for any work you drive from the CLI. CLI and portal stay in lock-step — either surface is a valid operator entry point.

## See it in action

Two fast e2e scenarios exercise the messaging plumbing without needing an LLM backend:

- [`fast/13-agent-domain-message.sh`](../../../tests/e2e/scenarios/fast/13-agent-domain-message.sh) — sends a Domain message to an agent and verifies the `MessageReceived` activity event lands. Proves the router → actor → activity-bus path end-to-end.
- [`fast/14-conversation-lifecycle.sh`](../../../tests/e2e/scenarios/fast/14-conversation-lifecycle.sh) — starts a fresh conversation on an idle agent and verifies the three lifecycle events fire in order: `MessageReceived` → `ConversationStarted` → `StateChanged (Idle→Active)`.

Scenario [`llm/20-message-human-to-agent.sh`](../../../tests/e2e/scenarios/llm/20-message-human-to-agent.sh) (requires Ollama) drives the full human-to-agent round-trip through `spring message send`. See [Runnable Examples](examples.md) for the full catalogue.
