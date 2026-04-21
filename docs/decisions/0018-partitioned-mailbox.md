# 0018 — Three-channel partitioned mailbox per agent

- **Status:** Accepted — every agent has a control / conversation / observation mailbox; sender priority does not exist.
- **Date:** 2026-04-21
- **Related code:** `src/Cvoya.Spring.Core/Messaging/`, `src/Cvoya.Spring.Dapr/Actors/AgentActor.cs` (mailbox processing).
- **Related docs:** [`docs/architecture/messaging.md`](../architecture/messaging.md), [`docs/concepts/messaging.md`](../concepts/messaging.md).

## Context

V1 used a single FIFO inbox. Two recurring failures fell out of that:

1. **Control messages got stuck behind work.** A "cancel" or "status" issued while the agent was running queued behind a long-running tool call. By the time the agent processed it, the original work was done — too late to be useful.
2. **Observation events fragmented agent reasoning.** Subscriptions delivered one event at a time, so the agent's prompt thrashed between "you have a new GitHub comment" and "your previous task is still in progress." Batching observations in the prompt context produced demonstrably better LLM reasoning, but the FIFO queue made batching awkward — events had to be re-stitched after the fact.

A naïve "priority queue with sender-supplied priority" was rejected on first principles: any sender can claim `Priority=High`; once that lever exists, every sender uses it.

## Decision

**Each agent has a partitioned mailbox with three channels — control, conversation, observation — chosen by the platform from `MessageType`. Senders never set priority. The agent processes channels in a deterministic order: control first, then the active conversation slot, then a batched observation digest.**

- **Control channel** — cancellations, status queries, lifecycle events. Always served before conversation work; latency-sensitive.
- **Conversation channel** — domain message traffic, organised by conversation id. The agent has one active conversation at a time (see [ADR 0011](0011-persistent-agent-lifecycle-http-surface.md) for the persistent-agent variant); other conversations queue as pending.
- **Observation channel** — event-stream subscriptions (activity events, directory updates, …). Events accumulate; the agent processes a batched "what happened since I last looked?" digest, not an event at a time.

Sender priority is platform-controlled by message type, not by the sender. There is no API for "send this with high priority."

## Alternatives considered

- **Single FIFO queue.** Loses both wins (control latency, observation batching).
- **Priority queue with sender-supplied priority.** Trivially abused. Equivalent to "always set Priority=High."
- **Channel per conversation, no batched observation.** Solves the first problem (control isolation) but not the second (observation fragmentation). Also explodes channel count.

## Consequences

- **Control latency is predictable.** A cancel issued during a long tool call is processed at the next turn boundary, not at the end of the queue.
- **Better LLM reasoning on observations.** Batched observation digest gives the model "everything new since last look" in one prompt slot instead of one prompt per event.
- **No sender lever for priority.** Every API surface (CLI, REST, MCP, A2A) routes by `MessageType` and lets the platform pick the channel; this is non-negotiable and load-bearing for fairness across senders.
- **Conversation isolation is mailbox-level.** Two unrelated conversations cannot interleave their messages within the agent's working context, regardless of how senders order their `send` calls.
