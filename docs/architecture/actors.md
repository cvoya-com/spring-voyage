# Actor Model

Every addressable entity in Spring Voyage V2 -- agents, units, connectors, and humans -- is implemented as a Dapr virtual actor. This document describes the actor model and how it maps to the platform's concepts.

## Why Actors

The actor model provides three properties critical to the platform:

1. **Turn-based concurrency** -- each actor processes one message at a time. No locks, no race conditions, no shared mutable state. This is a natural fit for agents that need to reason about one thing at a time.
2. **Automatic lifecycle** -- virtual actors are activated on first message and deactivated after idle timeout. No manual instance management. The platform creates millions of actors without worrying about resource allocation.
3. **Durable state** -- each actor's state is automatically persisted to the configured state store. If an actor crashes and reactivates, it resumes from its last persisted state.

## Actor Types

| Actor Type | Represents | Key Responsibilities |
|-----------|------------|---------------------|
| **AgentActor** | A single AI entity | Runtime state, AI cognition, pub/sub subscriptions, mailbox management |
| **UnitActor** | A composite agent (a group) | Member management, policies, expertise directory, orchestration dispatch |
| **ConnectorActor** | An external system bridge | Event translation, outbound skills, connection lifecycle |
| **HumanActor** | A human participant | Notification routing, permission enforcement |

All four implement the core messaging interface: they can receive messages and they have an address.

## The IAddressable Foundation

Every actor is **addressable** -- it has a globally unique identity. On top of that, actors implement additional capability interfaces as appropriate:

| Capability | AgentActor | UnitActor | HumanActor | ConnectorActor |
|-----------|-----------|----------|-----------|---------------|
| Message receiving | Yes | Yes | Yes | Yes |
| Expertise provider | Yes | Yes (aggregated) | No | No |
| Activity observable | Yes | Yes (aggregated) | No | Yes |
| Capability provider | Yes | Yes (union or projected) | No | Yes |

## Actor IDs and Addressing

All actors have flat, globally unique Dapr actor IDs derived from their UUID. Both path addresses and direct addresses resolve to the same actor ID:

- **Path address** (`agent://engineering-team/ada`) -- looked up in the directory, resolves to actor ID
- **Direct address** (`agent://@f47ac10b-...`) -- maps directly to actor ID without lookup

There is no multi-hop forwarding. Routing from sender to recipient is a single directory lookup followed by a direct actor method call.

## AgentActor in Detail

The AgentActor is the most complex actor type. It manages:

- **Mailbox** -- the three-channel message processing system (control, conversation, observation)
- **Active conversation** -- the currently executing work, with state checkpoints
- **Pending conversations** -- queued work waiting for the active conversation to complete
- **Observations** -- batched events from subscriptions, waiting for initiative processing
- **Expertise profile** -- the agent's domain knowledge description
- **Memory** -- persistent learnings and context across conversations
- **Initiative state** -- current initiative level, last reflection time, pending actions

### Asynchronous Work Dispatch

The actor never performs long-running work inside an actor turn. When processing a work message:

1. The actor validates, updates state, and launches work asynchronously (via a Task, background thread, or execution environment container)
2. The actor registers a CancellationTokenSource for the work
3. The actor turn completes in milliseconds
4. The actor is immediately free to process the next message

This means cancellation is always immediate -- when a cancel message arrives, the actor is free to process it and trigger the CancellationTokenSource.

## UnitActor in Detail

The UnitActor manages the unit's identity, membership, and boundary. It delegates message handling to a pluggable orchestration strategy.

The unit actor is responsible for:
- Maintaining the member list and expertise directory
- Applying boundary rules (opacity, projection, filtering)
- Aggregating member activity streams
- Delegating incoming messages to the orchestration strategy

The orchestration strategy decides how to route messages to members -- the unit actor doesn't make routing decisions itself.

## ConnectorActor in Detail

The ConnectorActor bridges an external system to the unit:

- Translates inbound events from the external system into platform messages
- Provides outbound skills (tools) that agents can call
- Manages connection state and authentication
- Emits activity events for observability

## HumanActor in Detail

The HumanActor is the simplest actor type:

- Routes messages to the human's notification channels (Slack, email, dashboard)
- Enforces the human's permission level when they interact with agents
- Tracks the human's presence and availability
