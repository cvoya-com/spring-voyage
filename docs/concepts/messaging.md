# Messaging and Addressing

Communication in Spring Voyage is built on two primitives: **addresses** (how entities are identified) and **messages** (how they communicate).

## Addressable Entities

Everything that can send or receive a message is **addressable**. The four types of addressable entities are:

| Entity | Description |
|--------|-------------|
| **Agent** | An autonomous AI entity (or a unit acting as one) |
| **Unit** | A composite agent — group of agents that appears as one to the outside |
| **Human** | A human participant in a unit |
| **Connector** | A bridge to an external system (GitHub, Slack, etc.) |

A pub/sub **topic** is a separate primitive (see [Pub/Sub Topics](#pubsub-topics) below); it is not an addressable actor.

## Addresses

Every addressable entity has a stable `Guid` identity. An address is the pair `(scheme, Guid)` and renders on the wire as `scheme:<32-hex-no-dash>`:

- `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7` -- a specific agent
- `unit:dd55c4ea8d725e43a9df88d07af02b69` -- a unit (also reachable via `agent:<id>` because a unit IS an agent)
- `human:f47ac10b58cc4372a5670e02b2c3d479` -- a human participant
- `connector:a1b2c3d4e5f6789012345678901234ab` -- a connector

There is no path-shaped address, no `@<uuid>` form, no namespace+name pair. The membership graph (which units a particular agent belongs to, which sub-units a unit contains, what tenant owns what) is walked at routing time inside the directory; it does not appear in the address string.

Parsers are lenient — addresses carrying the dashed Guid form (`agent:8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7`) parse just as well — but the canonical render always uses the no-dash form. Identifier conventions, the JSON-vs-URL split, manifest grammar, and CLI semantics are documented in [Identifiers](../architecture/identifiers.md).

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

### Routing is Platform-Controlled

The sender does not specify priority or urgency. The platform determines which mailbox channel a message enters based on:

1. **MessageType** -- control types always route to the control channel
2. **Delivery mechanism** -- for Domain messages:
   - Direct message (actor method call) goes to the conversation channel
   - Pub/sub subscription goes to the observation channel
   - Reminder or timer goes to the observation channel
   - Input binding (external event via connector) goes to the conversation channel

No sender can escalate their own message priority. The platform is the sole authority on routing.

## How Routing Works

All actors have flat, globally unique Dapr actor ids derived from their `Guid`. The directory resolves an address to an actor id in a single lookup; messages dispatch directly to that actor. There is no multi-hop forwarding through a chain of units.

**Permission enforcement** happens at resolution time. The directory walks the membership graph from the addressed actor toward the tenant root and at each boundary edge evaluates the permission rule against the sender; the walk returns either an actor id (permitted) or a structured deny (rejected). This is one synchronous check whose cost is O(membership depth), not per-hop forwarding.

When the addressed actor is a unit (rather than a specific member), the unit applies its boundary filtering and delegates to its orchestration strategy, which picks a member to handle the message.

## Pub/Sub Topics

Topics provide event distribution. An agent subscribes to a topic and receives all messages published to it. Topics are namespaced by tenant + owner Guid + topic name (e.g. `dd55c4ea8d725e43a9df88d07af02b69/8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7/pr-reviews`); system topics use the literal `system/` prefix.

The pub/sub infrastructure is broker-agnostic -- Redis for development, Kafka or Azure Event Hubs for production. The choice is configuration, not code.

## Multicast

A multicast send dispatches a single domain message to every actor that matches a routing pattern (for example, all members of a unit advertising a given role). The multicast resolver above the directory expands the pattern into a set of `(scheme, Guid)` addresses, each routed individually. Multicast is useful for broadcast queries ("who can help with this Python issue?") or role-based work distribution.
