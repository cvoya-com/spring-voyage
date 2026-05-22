# 0053 — Units are agents; the platform delivers one-way messages, it does not orchestrate

- **Status:** Accepted — 2026-05-22. **Re-baseline.** This record restates the current design directly. It consolidates and supersedes the archived [0039](archive/0039-units-are-agents.md) (units are agents), [0048](archive/0048-event-vs-request-message-semantics.md) (one-way domain messaging), and [0049](archive/0049-message-delivery-tool-contract.md) (delivery-acknowledgement contract). Those three ADRs recorded the design as it evolved, in fast succession, away from a platform-owned orchestration layer; the live decision ended up spread across them with amendment headers and strike-through bodies. Nothing here reverses 0039 / 0048 / 0049 — it states what they collectively decided, cleanly. The archived records keep the full rejected-alternatives reasoning.
- **Date:** 2026-05-22
- **Related ADRs:** [0054](0054-one-mcp-server-one-execution-host.md) — the platform MCP surface and execution hosts; the `sv.messaging.send` / `sv.messaging.multicast` tools named below are defined there. [0017](0017-unit-is-an-agent-composite.md) — the composite pattern this builds on. [0038](0038-agent-runtime-and-model-provider-split.md) — the `(runtime, model)` configuration a unit and a leaf agent share. [0030](0030-thread-model.md) — the participant-set thread the dispatch response is recorded on. [0045](0045-connector-domain-agnostic-platform.md) — connectors facilitate flow but do not replicate upstream config.
- **Related docs:** [`docs/architecture/messaging.md`](../architecture/messaging.md), [`docs/architecture/units-and-agents.md`](../architecture/units-and-agents.md).

## Context

A unit was originally modelled as an agent *plus* a configured orchestration strategy: `IOrchestrationStrategy` (with `AiOrchestrationStrategy` / `LabelRoutedOrchestrationStrategy` / `WorkflowOrchestrationStrategy` concretes), a strategy store, and `OrchestrationEndpoints`. A unit's execution config — its own `(runtime, model)` — was validated at create time but read at dispatch time *only when there was a member to route to*. A unit with no members dropped the turn: the message disappeared, the unit's own runtime never ran.

Two configurations on one entity that had to be compatible at dispatch time, with no coupling at the type level. The fix was not a better strategy taxonomy — it was to delete the axis. **A unit is an agent that has children.** Orchestration is something an agent's runtime *does*, not something the platform *configures*.

That move had a corollary. If the unit's own runtime produces the dispatch response, there is no separate sub-agent for the platform to route a reply back to. Inspecting the actual message flow showed that domain request/reply was already a fiction: the API already returned a `null` domain payload, and the only manufactured reply was a single coordinator hop — the same hop that misrouted a connector-origin response and dropped it silently. Domain messaging is, and should be modelled as, **one-way events**.

## Decision

### 1. A unit is an agent

A unit and a leaf agent are the same kind of thing on every dimension that matters for dispatch: an address, a mailbox (`IAgent.ReceiveAsync`), and execution configuration (`(runtime, model, image, hosting)` per [ADR-0038](0038-agent-runtime-and-model-provider-split.md)). The **only** structural difference is that a unit has children — zero or more member agents or sub-units. `IUnitActor` and `IAgentActor` remain as kind-specific groupings (a unit has membership, expertise aggregation, a boundary, a lifecycle workflow; a leaf agent does not), but neither is a separate concept on the dispatch dimension.

The address schemes `agent:` and `unit:` both stay — they encode containment shape and are referenced across the directory, manifests, OpenAPI, and CLI — but the scheme does not gate behaviour. Both resolve to the same actor kind on the mailbox dimension.

### 2. The platform owns no orchestration-policy abstraction

There is no `IOrchestrationStrategy`, no strategy store, no orchestration endpoints, no `orchestration:` manifest block. When a message reaches a unit's mailbox, the unit's **own runtime runs** — the same launcher path that runs a leaf agent. The runtime's instructions decide whether to answer directly, hand the work to a child, or fan it out. "Delegation" is not a platform mechanism; it is message *content* the recipient's runtime interprets.

A unit with zero members still receives messages and runs its runtime; it simply has no members to pass work to.

### 3. Domain messages are one-way events

A domain `Message` is an event — "something happened" (a connector-translated webhook, a human message, a timer, work reported by another agent). It is delivered to a unit/agent; **no sender is blocked on a return value**.

When a dispatch completes, the runtime's response is **recorded on the originating thread** (a `messages` row plus a terminal activity) and is **never routed back** to `Message.From`. A unit/agent that wants to respond acts through its tools, or sends a *new* one-way message on the thread. The thread ([ADR-0030](0030-thread-model.md)) is the correlation primitive; request/reply, where a flow genuinely needs it, is a pattern built on top (send → end turn → resume when the reply lands on the thread) — never a transport feature.

Control-plane *queries* — `MessageType.StatusQuery`, `HealthCheck` — are infrastructure RPC, not domain messaging. They keep their synchronous in-actor reply.

### 4. Message-delivery tools return a delivery acknowledgement, never a reply

A runtime delivers a message through the platform's delivery tools (`sv.messaging.send` / `sv.messaging.multicast` — see [ADR-0054](0054-one-mcp-server-one-execution-host.md)). Every delivery tool shares one contract:

- The response is a **delivery acknowledgement** — the message was durably placed in the recipient's mailbox. It never carries the recipient's work product and never waits for the recipient to be dispatched.
- Delivery is **synchronous with bounded retry** inside the handler (a few attempts over a few seconds) — there is no delivery queue. `agent:` / `unit:` / `human:` targets are virtual Dapr actors that always activate, so the only delivery failure is transient infrastructure; a non-existent, cross-tenant, or self-targeted address is caught by synchronous validation first.
- Both failure classes — validation failure and terminal delivery failure — surface **synchronously as tool errors**. Failure is never modelled as a message, so the platform needs no system sender address and no delivery-failure message type.

**The fast-enqueue invariant is load-bearing.** Synchronous delivery is deadlock-free only because `AgentActor.HandleDomainMessageAsync` (and the unit equivalent) enqueues the message into durable per-thread mailbox state, kicks off dispatch as a detached task, and returns the ack in milliseconds — it never awaits the recipient's runtime. `A → B → A` is two fast enqueues, not a turn-based-actor deadlock. If `ReceiveAsync` is ever made to await the runtime, synchronous delivery deadlocks. This is an invariant.

### 5. Connectors are non-routable bridges

A connector is never a routable actor. It has exactly two surfaces: an **inbound** event translator (external webhook → one-way domain message addressed at the bound unit) and an **outbound** agent-invoked skill/tool. A synthetic `connector:` address on an inbound message is provenance only — nothing is ever routed back to it.

### 6. Inheritance generalises across units and leaf agents

A top-level entity (no parent unit) — unit or leaf agent — is tenant-parented and inherits execution config from tenant defaults. A single-parent agent inherits by default, with a permitted per-field override. A multi-parent agent must define its own execution config unless every parent resolves identical effective config; a conflict is rejected at create / reparent time with a structured error naming the diverging field. Container-runtime selection (podman vs docker) is platform configuration, not an operator-facing field.

## Consequences

- One concept (agent) on the dispatch dimension; one runtime path; one launcher contract. A unit's `(runtime, model)` actually runs the unit instead of being validation-only config.
- A unit with no members no longer drops the turn — its own runtime answers.
- Webhook-originated dispatch responses are recorded on the thread instead of being dropped.
- Every responding unit pays a runtime-launcher cost per turn; the previous "cheap" routing-only path was a hidden subsidy that broke the moment a unit had no members.
- `Message` carries no request/reply axis and no new field — the change is in how the coordinator and the delivery tools behave, not the message shape.
- The actor-reentrancy deadlock class stays excluded *by* the fast-enqueue invariant; it is not a free property.

## Revisit criteria

- A genuine need for cross-thread runtime interaction (a runtime acting on a thread it was not invoked for) would reopen the one-thread-per-turn scoping.
- A runtime that needs a delivery primitive the two `sv.messaging.*` tools cannot express would prompt an amendment to [ADR-0054](0054-one-mcp-server-one-execution-host.md), not this record.
