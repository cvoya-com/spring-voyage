# Messaging and Addressing

Communication in Spring Voyage is built on two primitives: **addresses** (how entities are identified) and **messages** (how they communicate).

## Addressable Entities

Everything that can send or receive a message is **addressable**. There are four types of addressable entities:

| Entity | Description |
|--------|-------------|
| **Agent** | An autonomous AI entity (or a unit acting as one) |
| **Human** | A human participant in a unit |
| **Connector** | A bridge to an external system (GitHub, Slack, etc.) |
| **Topic** | A named pub/sub channel for event distribution |

## Address Formats

Every addressable entity has two address forms that resolve to the same underlying identity.

### Path Addresses

Human-readable addresses that reflect organizational structure:

- `agent://engineering-team/ada` -- an agent named "ada" in the engineering team
- `agent://engineering-team/backend-team/ada` -- a nested agent
- `agent://engineering-team` -- the unit itself (a unit IS an agent)
- `human://engineering-team/savasp` -- a human participant
- `connector://engineering-team/github` -- a connector
- `role://engineering-team/backend-engineer` -- multicast to all agents with that role
- `topic://engineering-team/pr-reviews` -- a pub/sub topic

Within a tenant, the tenant prefix is implicit. Cross-tenant addressing adds the tenant name: `agent://acme/engineering-team/ada`.

### Direct Addresses (UUID)

Stable addresses using the entity's globally unique identifier:

- `agent://@f47ac10b-58cc-4372-a567-0e02b2c3d479`

Direct addresses are useful when the hierarchy is deep, when an agent moves between units (UUID is stable, path changes), or for programmatic references.

Both forms are interchangeable in messages.

### System Addresses

Platform-level services have special addresses:

- `system://root` -- the tenant root unit
- `system://directory` -- the tenant root directory
- `system://package-registry` -- the package registry

## Messages

A message is a typed communication between addressable entities. Every message has:

| Field | Description |
|-------|-------------|
| **Id** | Globally unique identifier (for deduplication, acknowledgment, audit) |
| **From** | The sender's address |
| **To** | The recipient's address |
| **Type** | Platform action or domain message (see below) |
| **ConversationId** | Correlates related messages into a conversation |
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

All actors have flat, globally unique identifiers. Path addresses are resolved to actor IDs through directory lookups -- there is no multi-hop forwarding through each unit in the path.

Each unit maintains a local directory cache mapping member paths to actor IDs. The root unit maintains the tenant-wide directory. Path resolution is a single lookup.

**Permission enforcement** happens at resolution time: when the directory resolves a path, it checks the sender's permissions against each boundary along the path and either returns the actor ID or rejects the message.

## Pub/Sub Topics

Topics provide event distribution. An agent subscribes to a topic and receives all messages published to it. Topics are namespaced by unit: `engineering-team/pr-reviews`, `research-team/papers/new-arxiv`.

The pub/sub infrastructure is broker-agnostic -- Redis for development, Kafka or Azure Event Hubs for production. The choice is configuration, not code.

## Multicast via Roles

Addressing a role (e.g., `role://engineering-team/backend-engineer`) sends the message to all agents with that role. This is useful for broadcast queries ("who can help with this Python issue?") or role-based work distribution.
