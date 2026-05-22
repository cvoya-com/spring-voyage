# 0049 — Message-delivery tools return a delivery acknowledgement, never a reply

> **Amendment (2026-05-21, [#2578](https://github.com/cvoya-com/spring-voyage/issues/2578)) — the delivery tool family is `sv.messaging.*`.** [ADR-0050](0050-platform-mcp-tool-surface.md) renames the message-delivery tools: `delegate_to` / `fanout_to` are deleted and replaced by `sv.messaging.send` / `sv.messaging.multicast`. The delivery-acknowledgement contract decided here is unchanged and applies to them as-is. The message-carried hop counter noted as follow-up [#2576](https://github.com/cvoya-com/spring-voyage/issues/2576) in Consequences is now implemented on the single delivery seam.

- **Status:** Proposed — the message-delivery tool family (`delegate_to`, `fanout_to`, and future `send_message_to` / `broadcast_to`) shares one contract. Each tool is an **RPC** whose response is a **delivery acknowledgement**: the message was durably placed in each recipient's mailbox. A delivery tool **never** waits for the recipient to *process* the message and **never** carries the recipient's work product — that is a one-way `Message` (ADR-0048), produced if and when the recipient decides. Delivery is **synchronous with bounded retry** inside the dispatcher handler (R attempts over a T budget, platform defaults); there is no delivery queue. The only failure delivery can encounter is transient infrastructure — `agent://` / `unit://` / `human://` targets are virtual Dapr actors that always activate; a non-existent or cross-tenant target is caught by *synchronous validation*. Both failure classes — validation failure and terminal delivery failure — are returned **synchronously as tool errors**, so the calling model sees them as the tool result. Because failure is never modelled as a message, there is **no platform sender address** and no delivery-failure message type.
- **Date:** 2026-05-20
- **Related:** [#2569](https://github.com/cvoya-com/spring-voyage/issues/2569) / [#2570](https://github.com/cvoya-com/spring-voyage/pull/2570) (the orchestration-tool reshape that implements this ADR); [#2576](https://github.com/cvoya-com/spring-voyage/issues/2576) (follow-up — message-carried hop counter for delegation-loop prevention).
- **Related ADRs:** [0048 — Domain messaging is one-way](0048-event-vs-request-message-semantics.md) — this ADR is the *tool-layer* corollary: 0048 makes the messaging layer one-way; 0049 fixes what the tools that send those messages return. [0039 — Units are agents](0039-units-are-agents.md) — the orchestration tool surface (`delegate_to` / `fanout_to`) and the `RoutingDecision` record.
- **Related code:** `src/Cvoya.Spring.Dapr/Orchestration/OrchestrationToolHandlers.cs` (synchronous bounded-retry delivery; returns a delivery ack, not the target response), `src/Cvoya.Spring.Dapr/Orchestration/OrchestrationDepthCounter.cs` (**deleted** — call-stack-scoped, ineffective under one-way delivery), `src/Cvoya.Spring.Dispatcher/OrchestrationCallbackEndpoints.cs` + `OrchestrationCallbackModels.cs` (REST + MCP responses carry the ack DTO), `src/Cvoya.Spring.AgentSdk/OrchestrationClient.cs` (`DelegateAsync` / `FanoutAsync` return the ack), `src/Cvoya.Spring.Dapr/Orchestration/Resources/{delegate_to,fanout_to}.output.schema.json` (ack shape), `src/Cvoya.Spring.Dapr/Actors/AgentActor.cs` (`HandleDomainMessageAsync` — the load-bearing fast-enqueue invariant, see Decision §5).

## Context

A delegation tool call used to round-trip: an orchestrator called `delegate_to(B)`, and the platform routed B's eventual response back to the orchestrator. ADR-0048 removed that routing hop — domain messaging is one-way — which left the orchestration tools without a defined contract: what does `delegate_to` return now, and how does it return it?

The tempting answer is to make `delegate_to` a blocking tool call that awaits and returns B's terminal response. That reintroduces exactly the failure ADR-0048 removed, and worse: a turn-based-actor deadlock. Actors process one call at a time. If actor A invokes actor B and blocks on B's result, and B invokes A, A can never process B's call — it is still inside its first turn. `A → delegate_to(B) → B → delegate_to(A)` deadlocks.

The distinction that resolves this: **a tool may be an RPC; a tool that delivers a message must not block on a *reply to that message*.** Those are different waits. Confirming a message reached a mailbox is bounded, infrastructure-level, and does not depend on the recipient running. Waiting for the recipient's work product is unbounded and is what creates the deadlock. The first is fine in a tool; the second is not.

## Decision

### 1. One contract for the whole message-delivery tool family

`delegate_to`, `fanout_to`, and any future delivery tool (`send_message_to`, `broadcast_to`, …) behave identically at the delivery layer. They differ only in the `RoutingDecision` / activity they record and in their target arity. A new delivery tool inherits this contract; it does not redefine it.

### 2. The response is a delivery acknowledgement

The RPC response confirms the message was **durably placed in each recipient's mailbox** — nothing more. It never contains the recipient's response, and the tool never waits for the recipient to be dispatched or to finish. `fanout_to` delivers to N targets in parallel and its ack reports a per-target delivery *outcome* (delivered / failed) — outcomes, not work products.

### 3. The recipient's response, if any, is a separate one-way message

Per ADR-0048, a recipient that wants to respond sends a new one-way message on the thread (instructed by the delegating message, or by a future boundary policy). The orchestrator observes the thread; "round trip" is a thread-correlated pattern, never a tool feature.

### 4. Delivery is synchronous with bounded retry — no queue

The dispatcher handler delivers inline. A delivery is one fast actor hop (Decision §5); a *transient* infrastructure failure is retried up to **R** attempts within a **T** budget with backoff (platform defaults — initially 3 attempts over a few seconds; defined as constants/options). There is no delivery queue: the only thing that can fail a mailbox delivery is transient infrastructure, and bounded synchronous retry is the right instrument for it. A non-existent, cross-tenant, self-targeted, or malformed target is caught by synchronous *validation* before any delivery attempt.

### 5. The fast-enqueue invariant is load-bearing

Synchronous delivery is safe **only because** `AgentActor.HandleDomainMessageAsync` (and the unit equivalent) enqueues the message into durable per-thread mailbox state, kicks off dispatch as a detached task, and returns a delivery ack in milliseconds — it never awaits the recipient runtime. A delivery tool therefore blocks only on this fast enqueue, never on a turn. `A → B → A` is two fast enqueues; the actor-reentrancy deadlock class stays excluded. **If this invariant is ever broken — if `ReceiveAsync` is made to await the runtime — synchronous delivery deadlocks.** It is an invariant, not an implementation detail.

### 6. Both failure classes are synchronous tool errors

- **Validation failure** (malformed address, cross-tenant, self-target, unknown target, loop limit) — the request was never accepted; returned as a tool error.
- **Terminal delivery failure** (transient infrastructure persisting past R/T) — the platform is degraded; returned as a tool error telling the model to retry.

Both reach the calling model as the tool result. Neither is modelled as a message, so the platform needs no sender address and no delivery-failure message type.

## Consequences

- `OrchestrationDepthCounter` is **deleted**. It is call-stack-scoped (a `using` scope around the delivery call); under fire-and-forget delivery the scope disposes immediately, so it never observes depth > 1 and limits nothing. `MessageDeliveryException.RejectCodes.OrchestrationDepthExceeded` is **retained**, reserved for its replacement.
- Delegation-loop prevention moves to a **message-carried hop counter** rejected past a platform limit — tracked as follow-up [#2576](https://github.com/cvoya-com/spring-voyage/issues/2576). There is a brief window with no loop guard; acceptable pre-release for the unusual cyclic-delegation topology.
- `delegate_to` / `fanout_to` output schemas, the dispatcher REST + MCP responses, `OrchestrationCallbackModels`, and `OrchestrationClient.DelegateAsync` / `FanoutAsync` all return the ack DTO instead of a response message / results array.
- `RoutingDecision` recording on the activity stream is unchanged — the decision still happened and is still recorded.
- `Message` is unchanged — no new field (consistent with ADR-0048).

## Alternatives considered

- **A delivery queue (per-platform or per-tenant, asynchronous).** The tool returns "accepted" immediately; a queue worker delivers with retry; terminal failure is delivered to the sender as a one-way message. Rejected: it solves "recipient unreachable for a long period," which this architecture does not have — `agent://` / `unit://` / `human://` targets are virtual Dapr actors that always activate, and the only real delivery failure is transient infrastructure. The queue costs a durable queue + worker, a **platform sender address** (a new system-origin address scheme, system-origin messages, prompt concepts for "a message from the platform"), and a delivery-failure message type — substantial modelling to handle Dapr hiccups that a bounded synchronous retry handles directly.
- **`delegate_to` returns the recipient's response (request/reply via tooling).** Rejected: this is the round-trip ADR-0048 removed; it splits an orchestrator's turn across the network and reintroduces the actor-reentrancy deadlock of Decision §5.
- **Ack semantics = "the platform received your request"** (provisional accept; delivery and any failure resolved asynchronously). Rejected in favour of "durably enqueued": the caller learns deterministically and immediately whether its send succeeded, rather than a provisional accept a later message could retract — and it is what removes the platform sender address entirely.
