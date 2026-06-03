# Messaging and Addressing

Communication in Spring Voyage is built on two primitives: **addresses** (how entities are identified) and **messages** (how they communicate).

## Addressable Entities

Three kinds of entity are **routable actors** — each has a mailbox and can receive a message:

| Entity | Description |
|--------|-------------|
| **Agent** | An autonomous AI entity (or a unit acting as one) |
| **Unit** | A composite agent — group of agents that appears as one to the outside |
| **Human** | A human participant in a unit |

A **connector** also has a synthetic address (`connector:<id>`), but it is **not** a routable actor — it is a bridge to an external system (GitHub, Slack, etc.). A connector address appears only as a message's `From`, identifying the external origin of an inbound event; nothing routes a message *to* a connector. See [Connectors](connectors.md) for details.

A pub/sub **topic** is a separate primitive (see [Pub/Sub Topics](#pubsub-topics) below); it is not an addressable actor.

## Addresses

Every addressable entity has a stable `Guid` identity. An address is the pair `(scheme, Guid)` and renders on the wire as `scheme:<32-hex-no-dash>`:

- `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7` -- a specific agent
- `unit:dd55c4ea8d725e43a9df88d07af02b69` -- a unit (also reachable via `agent:<id>` because a unit IS an agent)
- `human:f47ac10b58cc4372a5670e02b2c3d479` -- a human participant
- `connector:a1b2c3d4e5f6789012345678901234ab` -- a connector

There is no path-shaped address, no `@<uuid>` form, no namespace+name pair. The membership graph (which units a particular agent belongs to, which sub-units a unit contains, what tenant owns what) is walked at routing time inside the directory; it does not appear in the address string.

Parsers are lenient — addresses carrying the dashed Guid form (`agent:8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7`) parse just as well — but the canonical render always uses the no-dash form.

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

Control-plane queries (`StatusQuery`, `HealthCheck`) are the exception: they are synchronous infrastructure probes and return their result to the caller directly.

### Routing is Platform-Controlled

The sender does not specify priority or urgency. The platform determines which mailbox channel a message enters based on:

1. **MessageType** -- control types (`Cancel`, `StatusQuery`, `HealthCheck`, `PolicyUpdate`) always route to the **control channel**, processed even mid-work.
2. **Delivery mechanism** -- for Domain messages:
   - A direct message enters the **per-thread FIFO channel** for its `ThreadId` — one queue per thread, processed concurrently across threads.
   - A pub/sub message, reminder, or timer enters the **observation channel**, batched for the initiative cognition loop.

No sender can escalate their own message priority. The platform is the sole authority on routing.

## How Routing Works

All actors have flat, globally unique Dapr actor ids derived from their `Guid`. The directory resolves an address to an actor id in a single lookup; messages dispatch directly to that actor. There is no multi-hop forwarding through a chain of units.

**Permission enforcement** happens at resolution time. The directory walks the membership graph from the addressed actor toward the tenant root and at each boundary edge evaluates the permission rule against the sender; the walk returns either an actor id (permitted) or a structured deny (rejected). This is one synchronous check whose cost is O(membership depth), not per-hop forwarding.

**The Hat ↔ unit gate** applies on top of that when the sender is a *person* (a tenant user messaging through the Web API or CLI, never an agent-to-agent send). A person addresses a unit or agent **as a Hat** — a human member of a unit — and a Hat can reach only the unit it belongs to plus that unit's direct members. If the person wears no Hat that reaches the target, the send is rejected (`403`); otherwise the platform stamps the reaching Hat as `Message.From`. The messaging surfaces only ever offer the Hats that can reach the chosen recipient. See [Humans → Reaching units and agents](humans.md#reaching-units-and-agents-the-hat--unit-gate) and [ADR-0062 § 11](../decisions/0062-tenant-user-human-explicit-binding.md).

When the addressed actor is a unit (rather than a specific member), the unit applies its boundary filtering and invokes its own runtime; the runtime decides whether to answer directly or delegate to a child by delivering a message via the `sv.messaging.*` tools the launcher attaches.

## Pub/Sub Topics

Topics provide event distribution. An agent subscribes to a topic and receives all messages published to it. Topics are namespaced by tenant + owner Guid + topic name (e.g. `dd55c4ea8d725e43a9df88d07af02b69/8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7/pr-reviews`); system topics use the literal `system/` prefix.

The pub/sub infrastructure is broker-agnostic -- Redis for development, Kafka or Azure Event Hubs for production. The choice is configuration, not code.

## Message delivery

A runtime delivers a domain message through three platform MCP tools. All return a **delivery acknowledgement** — the message was durably placed in the recipient's mailbox — never the recipient's reply.

| Tool | Thread shape |
|------|--------------|
| `sv.messaging.send` | One SHARED thread for `{caller} ∪ recipients`. Use when every recipient should see the others. |
| `sv.messaging.multicast` | N INDEPENDENT 1-1 threads `{caller, recipient_i}`. Use when recipients should not see each other. |
| `sv.messaging.respond_to` | Continue the conversation a `message_id` belongs to — the platform delivers to everyone already on it (minus the caller), on the same thread. Use to continue a conversation without rebuilding the recipient list. |

`send` and `multicast` take the same input shape — either an explicit `recipients` list or a relationship `scope` (`unit-members`, `siblings`) — and differ in thread identity, not input shape. `respond_to` instead names a `message_id` the runtime received and lets the platform pick the recipients (the conversation's participants). The calling participant is auto-included in every participant set, so the runtime does not list itself in `recipients`. The runtime never names a `thread_id`: the platform derives it from the participant set.

The inbound envelope names the conversation's roster directly: alongside `from` and `to` it carries `participants` — the routable members of the conversation ("everyone you could reach here"), which is the set `respond_to` delivers to. Human-initiated sends use the same primitive: a multi-party engagement created through the Web API resolves one shared thread from `{sender} ∪ recipients`, so every participant shares one conversation rather than splitting into per-recipient threads.

There is no `delegate_to` / `fanout_to` tool. A runtime that wants to *delegate* sends a message whose content says so — "delegation" is message content the recipient's runtime interprets, not a platform mechanism. Multicast is useful for broadcast queries ("who can help with this Python issue?") or role-based work distribution when the recipients should not be aware of each other; `send` with a multi-element `recipients` list is the right tool when they should share a thread.
