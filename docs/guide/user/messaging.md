# Messaging and Interaction

This guide covers how to send messages to agents, units, and humans on the Spring Voyage platform, how conversations form and evolve, and how to pick the right address for the job.

For the internals — mailbox partitioning, cancellation semantics, pub/sub streaming — see [Messaging architecture](../../architecture/messaging.md).

## Concepts at a glance

Spring Voyage models each routable participant — an agent, a unit (an agent that has children), a human — as an **actor** with a stable `Guid` identity. A **message** travels from a `From` address to a `To` address, carrying a **thread id** so the receiving actor knows which thread the message belongs to. A domain message is a **one-way event** — "something happened" — delivered to the recipient; the sender is never blocked on a return value ([ADR-0053](../../decisions/0053-units-are-agents-and-one-way-delivery.md)). A **connector** also has an address, but it is a non-routable bridge, not an actor — its address appears only as a message's `From`, identifying the external origin of an inbound event.

An address is the pair `(scheme, Guid)` and renders on the wire as `scheme:<32-hex-no-dash>` — for example `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7`. There is no path-shaped address; identity is the `Guid`. The platform does not inspect message content to decide routing; it reads the `To` scheme and id, looks the actor up in the directory, and delivers the message once. The actor on the receiving end — an `AgentActor`, `UnitActor`, or `HumanActor` — is responsible for turning the payload into work.

See [Data & identity](../../architecture/data-and-identity.md) for the full identifier model (wire forms, parser rules, OSS default tenant id, manifest grammar, CLI search-with-context), and [Messaging architecture — Addressing](../../architecture/messaging.md#addressing-and-activation) for the routing surface.

## Sending a message from the CLI

The CLI exposes a single command for sending messages:

```
spring message send <address> "<text>" [--thread <id>]
```

The address is `scheme:<32-hex-no-dash>` for one of `agent`, `unit`, or `human`. The text is wrapped in a domain message and delivered to the destination actor. A new thread is started when `--thread` is omitted; passing an existing thread id appends the message to that thread.

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

When the sender does not know (or does not want to pick) which member should handle the work, target the unit itself and let the unit's runtime decide:

```bash
spring message send unit:dd55c4ea8d725e43a9df88d07af02b69 "Implement the login feature described in issue #15"
```

The unit actor receives the message, applies boundary filtering, and invokes the unit's runtime through the same launcher path used for any agent. The runtime decides whether to answer directly or hand work to a child by sending it a message through the `sv.messaging.*` delivery tools (see [Units & agents](../../architecture/units-and-agents.md) and [ADR-0053](../../decisions/0053-units-are-agents-and-one-way-delivery.md)). There is no platform orchestration layer — "delegation" is message content the recipient's runtime interprets.

### Example: multicast

A multicast send delivers a single domain message to many targets at once — addressed explicitly, or by a directory-relationship `scope` (`unit-members`, `siblings`). Like every delivery, each is one-way: the tool response is a delivery acknowledgement, never an aggregated reply.

## Threads

A thread is the durable record of one participant set's shared exchanges. Every message carries a `ThreadId`; the receiving actor uses it to thread the message with prior work on the same thread.

- **Creation.** Sending a message without `--thread` starts a new thread. The server assigns a fresh thread id and returns it to the sender.
- **Continuation.** Sending additional messages with the same `--thread <id>` appends them to that thread. By default an agent processes each of its threads concurrently (one in-flight turn per thread), preserving per-thread FIFO order.
- **Lifelong record ([ADR-0030](../../decisions/0030-thread-model.md)).** A thread is a persistent record of a participant set's shared exchanges — there is no system-level "close" verb. The legitimate "I'm done with this thread" semantic is a per-(thread, participant) `ParticipantStateChanged` transition to `removed`.
- **One-way delivery.** When a turn completes, the runtime's response is **recorded on the originating thread** — it is never routed back to `Message.From`. A unit or agent that wants to respond sends a *new* one-way message on the thread.

See [Messaging architecture — The agent mailbox](../../architecture/messaging.md#the-agent-mailbox) for the mailbox partitioning that makes this deadlock-free.

### Follow-ups and reading a thread

There is no `reply` verb. Domain messaging is one-way; a "reply" is just another message on the same thread. Two equivalent paths exist for posting a follow-up:

- **`spring message send <address> "<text>" --thread <id>`** — reuses the generic send verb. Prefer this when the call site already has the address.
- **`spring thread send --thread <id> <address> "<text>"`** — the same effect, but reads as "post into thread X". Prefer this for scripts that iterate over threads.

To watch the traffic in real time, use the activity viewer:

```bash
spring activity list --source "agent:ada" --limit 20
spring activity tail  --source "agent:ada"
```

`activity list` surfaces the activity events — message received, decisions, completion — emitted on the shared activity stream; `activity tail` streams them live over SSE. The web portal shows the same events in the unit and agent detail pages.

Scripted review — "what happened on thread X?" — uses the `spring thread` verb family:

```bash
spring thread list                          # recent threads (default 50)
spring thread list --unit engineering-team
spring thread list --agent ada
spring thread list --participant agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7
spring thread show <thread-id>              # full thread: summary + ordered events
```

`list` renders one row per thread. `show` prints the thread header (participants, origin, created / last activity) followed by the full ordered event timeline. Both commands accept `--output json`.

### Inbox: things awaiting a human

When agents hand work to a human — approvals, clarifications, go / no-go decisions — the inbox is the corresponding surface. It lists threads awaiting a response from the current human:

```bash
spring inbox list                     # awaiting-me queue
spring inbox show <thread-id>         # open the thread in context
spring inbox respond <thread-id> "Approved — ship it."
```

`respond` resolves the pending ask's sender automatically, so the common case (reply to whoever asked) needs no address. Pass `--to <address>` when you want to direct the message to a different participant.

See [Observing Activity](observing.md#threads-and-inbox) for more examples.

## Addressing scheme — when to use each

| Scheme        | Shape                          | When to use                                                                                          |
| ------------- | ------------------------------ | ---------------------------------------------------------------------------------------------------- |
| `agent`       | `agent:<32-hex-no-dash>`       | You know exactly which member should handle the work.                                                |
| `unit`        | `unit:<32-hex-no-dash>`        | You want the unit's runtime to handle the work (answer directly or hand it to a child via the `sv.messaging.*` delivery tools). |
| `human`       | `human:<32-hex-no-dash>`       | You want to deliver a message to a human participant (notifications, approvals, escalations).         |

There is no `connector` send target: connectors are non-routable bridges ([ADR-0053](../../decisions/0053-units-are-agents-and-one-way-delivery.md)). A connector translates inbound external events into messages and exposes outbound skills agents invoke — you never send a message *to* one.

Address parsers are lenient: the dashed Guid form (`agent:8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7`) is accepted everywhere, but the canonical render uses the no-dash form. `display_name` is presentation-only — never an addressable handle. Use `spring agent show <name>` / `spring unit show <name>` to look up the canonical id when you only know a name.

See [Data & identity](../../architecture/data-and-identity.md) for the wire-form rules and [Messaging architecture — Addressing](../../architecture/messaging.md#addressing-and-activation) for the resolution algorithm and permission model.

## Cross-unit messaging

A sender in one unit can target an actor in a different unit by supplying the actor's `Guid`. The router resolves the destination in a single directory lookup and enforces the sender's permissions at each membership-graph edge from the addressed actor toward the tenant root — cross-unit delivery is one synchronous permission check per edge, not a forwarded hop through each unit's actor.

```bash
# Ada in engineering-team asks Kay (in research-team) for a design review.
spring message send agent:f47ac10b58cc4372a5670e02b2c3d479 \
  "Please review the API design in PR #73 when you have a moment."
```

If the sender lacks permission to reach the addressed agent (the receiving unit denies deep access, or the addressed member is private to its unit), the send returns a permission-denied error and the message never reaches the destination actor.

## Tips

- **Let the unit route when in doubt.** Addressing the unit (`unit:<id>`) and letting the unit's runtime decide whether to answer or hand work to a child is usually the right default for cross-team requests. Pin to a specific `agent:<id>` only when the work genuinely needs that specific agent.
- **Hold on to thread ids.** Pass the same `--thread <id>` on follow-ups so the agent's mailbox threads your messages together. Without it, each send creates a fresh thread — noisier and harder to follow.
- **Delivery is one-way.** A `send` returns a delivery acknowledgement — the message landed in the recipient's mailbox — never the recipient's reply. Watch the activity feed (or the thread) for the response; it lands as a new message on the same thread.
- **The web portal shows the same traffic.** The portal's unit and agent pages display activity events (messages, decisions, completions) for any work you drive from the CLI. CLI and portal stay in lock-step — either surface is a valid operator entry point.

## See it in action

Two `pool: fast` CLI scenarios exercise the messaging plumbing without needing an LLM backend:

- [`messaging/agent-domain-message.sh`](../../../tests/e2e/cli/scenarios/messaging/agent-domain-message.sh) — sends a Domain message to an agent and verifies the `MessageReceived` activity event lands. Proves the router → actor → activity-bus path end-to-end.
- [`messaging/conversation-lifecycle.sh`](../../../tests/e2e/cli/scenarios/messaging/conversation-lifecycle.sh) — starts a fresh conversation on an idle agent and verifies the three lifecycle events fire in order: `MessageReceived` → `ThreadStarted` → `StateChanged (Idle→Active)`.

Scenario [`messaging/message-human-to-agent.sh`](../../../tests/e2e/cli/scenarios/messaging/message-human-to-agent.sh) (`pool: llm`, requires Ollama) drives the full human-to-agent round-trip through `spring message send`. See [Runnable Examples](examples.md) for the full catalogue.
