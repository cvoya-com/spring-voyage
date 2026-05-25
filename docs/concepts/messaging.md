# Messaging and Addressing

Communication in Spring Voyage is built on two primitives: **addresses** (how entities are identified) and **messages** (how they communicate).

## Addressable Entities

Three kinds of entity are **routable actors** — each has a mailbox and can receive a message:

| Entity | Description |
|--------|-------------|
| **Agent** | An autonomous AI entity (or a unit acting as one) |
| **Unit** | A composite agent — group of agents that appears as one to the outside |
| **Human** | A human participant in a unit |

A **connector** also has a synthetic address (`connector:<id>`), but it is **not** a routable actor — it is a bridge to an external system (GitHub, Slack, etc.). A connector address appears only as a message's `From`, identifying the external origin of an inbound event; nothing routes a message *to* a connector. See [Connectors](connectors.md) and [ADR-0053 § 5](../decisions/0053-units-are-agents-and-one-way-delivery.md).

A pub/sub **topic** is a separate primitive (see [Pub/Sub Topics](#pubsub-topics) below); it is not an addressable actor.

## Addresses

Every addressable entity has a stable `Guid` identity. An address is the pair `(scheme, Guid)` and renders on the wire as `scheme:<32-hex-no-dash>`:

- `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7` -- a specific agent
- `unit:dd55c4ea8d725e43a9df88d07af02b69` -- a unit (also reachable via `agent:<id>` because a unit IS an agent)
- `human:f47ac10b58cc4372a5670e02b2c3d479` -- a human participant
- `connector:a1b2c3d4e5f6789012345678901234ab` -- a connector

There is no path-shaped address, no `@<uuid>` form, no namespace+name pair. The membership graph (which units a particular agent belongs to, which sub-units a unit contains, what tenant owns what) is walked at routing time inside the directory; it does not appear in the address string.

Parsers are lenient — addresses carrying the dashed Guid form (`agent:8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7`) parse just as well — but the canonical render always uses the no-dash form. Identifier conventions, the JSON-vs-URL split, manifest grammar, and CLI semantics are documented in [Data & identity](../architecture/data-and-identity.md).

## Messages

A message is a typed communication between addressable entities. Every message has:

| Field | Description |
|-------|-------------|
| **Id** | Globally unique identifier (for deduplication, acknowledgment, audit) |
| **From** | The sender's address |
| **To** | The recipient's address |
| **Type** | Platform action or domain message (see below) |
| **ThreadId** | Identifies the thread (the participant-set relationship) this message belongs to |
| **Payload** | The message content (structured data) |
| **Timestamp** | When the message was created |

### Message Types

Messages are classified into types that determine how the platform handles them:

| Type | Description | Routing |
|------|-------------|---------|
| **Domain** | Agent interprets the payload; platform only routes | Based on delivery mechanism |
| **Cancel** | Platform triggers cancellation of active work | Always to control channel |
| **StatusQuery** | Platform responds with current agent state | Always to control channel |
| **HealthCheck** | Platform responds with liveness status | Always to control channel |
| **PolicyUpdate** | Platform applies runtime policy changes | Always to control channel |

The platform never inspects the payload of a Domain message. Domain-specific semantics (e.g., "implement-feature", "review-pr") are structured data within the payload, defined by domain packages as conventions.

### Domain Messaging Is One-Way

Domain messages are **one-way events** — a notification that something happened, delivered to a unit or agent. The sender is not blocked waiting on a return value, and the platform does not route a "reply" back to the sender. When a unit/agent finishes processing a domain message, its output is **recorded on the thread**; if a response or follow-up is warranted, the unit/agent acts through its tools or sends a *new* one-way message on the thread. Request/reply, where a flow needs it, is a pattern built on threads — not a transport primitive.

Control-plane queries (`StatusQuery`, `HealthCheck`) are the exception: they are synchronous infrastructure probes and return their result to the caller directly. See [ADR-0053](../decisions/0053-units-are-agents-and-one-way-delivery.md).

### Routing is Platform-Controlled

The sender does not specify priority or urgency. The platform determines which mailbox channel a message enters based on:

1. **MessageType** -- control types (`Cancel`, `StatusQuery`, `HealthCheck`, `PolicyUpdate`) always route to the **control channel**, processed even mid-work.
2. **Delivery mechanism** -- for Domain messages:
   - A direct message enters the **per-thread FIFO channel** for its `ThreadId` — one queue per thread, processed concurrently across threads.
   - A pub/sub message, reminder, or timer enters the **observation channel**, batched for the initiative cognition loop.

No sender can escalate their own message priority. The platform is the sole authority on routing. The agent mailbox is detailed in [Architecture: Messaging § The agent mailbox](../architecture/messaging.md#the-agent-mailbox).

## How Routing Works

All actors have flat, globally unique Dapr actor ids derived from their `Guid`. The directory resolves an address to an actor id in a single lookup; messages dispatch directly to that actor. There is no multi-hop forwarding through a chain of units.

**Permission enforcement** happens at resolution time. The directory walks the membership graph from the addressed actor toward the tenant root and at each boundary edge evaluates the permission rule against the sender; the walk returns either an actor id (permitted) or a structured deny (rejected). This is one synchronous check whose cost is O(membership depth), not per-hop forwarding.

When the addressed actor is a unit (rather than a specific member), the unit applies its boundary filtering and invokes its own runtime; the runtime decides whether to answer directly or delegate to a child by delivering a message via the `sv.messaging.*` tools the launcher attaches (see [ADR-0053](../decisions/0053-units-are-agents-and-one-way-delivery.md)).

## Pub/Sub Topics

Topics provide event distribution. An agent subscribes to a topic and receives all messages published to it. Topics are namespaced by tenant + owner Guid + topic name (e.g. `dd55c4ea8d725e43a9df88d07af02b69/8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7/pr-reviews`); system topics use the literal `system/` prefix.

The pub/sub infrastructure is broker-agnostic -- Redis for development, Kafka or Azure Event Hubs for production. The choice is configuration, not code.

## Message delivery

A runtime delivers a domain message through two platform MCP tools. Both return a **delivery acknowledgement** — the message was durably placed in the recipient's mailbox — never the recipient's reply.

| Tool | Thread shape |
|------|--------------|
| `sv.messaging.send` | One SHARED thread for `{caller} ∪ recipients`. Use when every recipient should see the others. |
| `sv.messaging.multicast` | N INDEPENDENT 1-1 threads `{caller, recipient_i}`. Use when recipients should not see each other. |

Both tools take the same input shape — either an explicit `recipients` list or a relationship `scope` (`unit-members`, `siblings`) — and differ in thread identity, not input shape. The calling participant is auto-included in every participant set, so the runtime does not list itself in `recipients`. The runtime never names a `thread_id`: the platform derives it from the participant set (see [ADR-0030](../decisions/0030-thread-model.md)).

There is no `delegate_to` / `fanout_to` tool. A runtime that wants to *delegate* sends a message whose content says so — "delegation" is message content the recipient's runtime interprets, not a platform mechanism. Multicast is useful for broadcast queries ("who can help with this Python issue?") or role-based work distribution when the recipients should not be aware of each other; `send` with a multi-element `recipients` list is the right tool when they should share a thread. The delivery contract and the full `sv.<area>.<verb>` tool surface are described in [Architecture: Messaging](../architecture/messaging.md).
