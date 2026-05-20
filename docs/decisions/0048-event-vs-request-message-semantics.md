# 0048 — All domain messages are one-way events; connectors are non-routable bridges

- **Status:** Proposed — domain messaging on the platform is **one-way**. A domain `Message` is an *event*: "something happened" (a GitHub webhook fired, a timer elapsed, a Slack message arrived, an observed agent finished work, a human said something). It is delivered to a unit/agent and is never a request that expects a routed reply. `AgentDispatchCoordinator` **records** the dispatch response on the originating thread (a `messages` row plus a neutral terminal activity) and **never** routes it back to `From`. If a unit/agent decides a response is warranted, it acts — via `gh`/container tools — or **sends a new one-way message** on the appropriate thread using the existing message-sending tools (`EscalateTool`, `RequestHelpTool`, `MessageRouterSkillInvoker`). There is no `request`/`reply` axis and no new field on `Message`. Control-plane *queries* (`MessageType.StatusQuery`, `HealthCheck`) keep their synchronous in-actor reply — they are infrastructure RPC, not messaging between units/agents. `MessageRouter.RouteAsync` stays a delivery primitive: for a domain delivery its `Message?` return is a delivery ack (already `null` in practice — the runtime is launched, not awaited); only control-plane queries populate it. The synthetic `connector://…` `From` is provenance only, never a routing target. `ConnectorActor` — a never-implemented shell — and the router's `connector://` proxy branch / worker actor registration are deleted: connectors are bridges with exactly two surfaces — an inbound event translator and an outbound agent-invoked skill/tool — and are never a routable actor that receives messages or replies. The platform system prompt teaches units/agents the one-way model.
- **Date:** 2026-05-20
- **Related:** [#2549](https://github.com/cvoya-com/spring-voyage/issues/2549) (umbrella for this ADR and the implementation work); [#2547](https://github.com/cvoya-com/spring-voyage/issues/2547) / [#2548](https://github.com/cvoya-com/spring-voyage/issues/2548) (observability companion — made the dropped response visible as an `ErrorOccurred` activity; this ADR removes the routing hop entirely, so the connector-origin case is no longer an error).
- **Related ADRs:** [0039 — Units are agents](0039-units-are-agents.md) — the unit's own runtime produces the dispatch response, so there is no separate sub-agent to reply to; recording on the thread is the correct terminal. [0045 — Connector as a domain-agnostic platform](0045-connector-domain-agnostic-platform.md) — this ADR makes the connector's *non-routability* explicit and names its two surfaces. [0030 — Thread model](0030-thread-model.md) — the recorded dispatch response is a `messages` row on the existing participant-set thread; the EF `messages` table stays authoritative; the thread is the correlation primitive that one-way messaging relies on.
- **Related code:** `src/Cvoya.Spring.Dapr/Execution/AgentDispatchCoordinator.cs:155-177` (`TryRouteResponseAsync` — record the dispatch response instead of routing it), `src/Cvoya.Spring.Dapr/Routing/MessageRouter.cs:66-112,329-334` (`RouteAsync` stays a delivery primitive; `PersistMessageAsync` is the persistence seam the coordinator calls directly), `src/Cvoya.Spring.Core/Messaging/Message.cs` (**unchanged** — no new field), `src/Cvoya.Spring.Dapr/Tools/EscalateTool.cs` + `Tools/RequestHelpTool.cs` + `Skills/MessageRouterSkillInvoker.cs` (the existing one-way send-message mechanism a unit/agent uses to respond), `src/Cvoya.Spring.Connector.GitHub/Webhooks/GitHubWebhookHandler.cs:664-696` (`CreateMessage` — `ConnectorAddress` documented provenance-only; no behavioural change), `src/Cvoya.Spring.Dapr/Actors/ConnectorActor.cs` + `Actors/IConnectorActor.cs` (deleted), `src/Cvoya.Spring.Dapr/Routing/AgentProxyResolver.cs:35,57` (the `connector://` proxy branch deleted), `src/Cvoya.Spring.Host.Worker/Composition/WorkerComposition.cs:163` (`RegisterActor<ConnectorActor>()` deleted), `src/Cvoya.Spring.Dapr/Prompts/PlatformPromptProvider.cs` (system prompt teaches the one-way model).

## Context

A GitHub webhook for issue #2535 was translated into a domain message, delivered to a unit, and dispatched through the unit's runtime. The runtime ran ~26s and produced a response. `AgentDispatchCoordinator` then tried to route that response back to `message.From` and failed:

```
warn MessageRouter: Address not found: connector://00000000000000000000006769746875
warn AgentDispatchCoordinator: Failed to route dispatcher response … ADDRESS_NOT_FOUND
```

The response was silently dropped. (#2547 / #2548 made the drop *visible* as an `ErrorOccurred` activity, but treated it as a failure.)

The obvious patch — give the connector a routable address, or mark webhook messages so the coordinator skips the reply — treats the symptom. The real observation is broader:

**Request/reply for domain messages is already a fiction.** Three facts establish this:

1. **The API already returns no domain response.** `MessageEndpoints.SendMessageAsync` routes a message and returns `result.Value?.Payload`. But `UnitActor.HandleDomainMessageAsync` returns `null`, and `RuntimeInvocationPath.InvokeAsync` returns once the runtime is *launched*, not finished. A domain send to a unit therefore already responds `200` with a `null` payload — the caller already observes the thread for the real answer.

2. **Units are agents (ADR-0039).** The unit's *own* runtime produced the response. There is no separate sub-agent. "Reply to the orchestrating unit" would route the response back into the same actor that produced it.

3. **Units/agents already respond by sending messages.** `EscalateTool`, `RequestHelpTool`, `MessageRouterSkillInvoker`, and `AgentObservationCoordinator` all already perform one-way `RouteAsync` sends. The platform's outbound side is one-way today.

The only place a domain "reply" is *manufactured* is `AgentDispatchCoordinator` auto-routing the dispatch response to `From`. That hop is vestigial — and it is exactly the misroute in the incident. So the fix is not to make the connector routable, nor to add a request/event axis to `Message`; it is to recognise that **domain messaging is one-way** and remove the manufactured reply.

## Decision

### 1. All domain messages are one-way events

A domain `Message` is an *event*: a notification that something happened, delivered to a unit/agent. It is **not** a request that expects a routed reply. There is no `request`/`reply` distinction, no delivery-mode axis, and **no new field on `Message`** — the contract is unchanged.

Event sources are open-ended and uniform: a GitHub webhook, a future time-based trigger, a future Slack-originating message, an observed agent finishing work, a human message from the portal/CLI, one unit delegating to another. Each is "a message arrived for you" — the receiver processes it; nobody is blocked on a return value.

### 2. The dispatch response is recorded, never routed

`AgentDispatchCoordinator` dispatches the runtime as today. When the runtime returns a response, the coordinator **records** it — persists the response envelope as a `messages` row on the originating thread and emits a neutral terminal activity — and **never** calls `RouteAsync` to deliver it to `From`. The non-zero-exit-code error response is recorded the same way. A connector-origin response is therefore no longer an error; it is the expected terminal of an event.

Because the coordinator no longer routes the response, it persists it directly through the same `IMessageWriter` seam `MessageRouter.PersistMessageAsync` uses, so the thread timeline keeps a durable row (ADR-0030 / ADR-0040 keep the EF `messages` table authoritative).

The agent's terminal output is recorded **implicitly** — automatically, by the coordinator. This is the audit trail and the "work finished" event, and it removes the silent-void failure mode where "done, nothing to say" would be indistinguishable from "done, forgot to report."

### 3. A unit/agent responds by sending a new one-way message

If, having processed an event, a unit/agent decides a response or follow-up is warranted, it either:

- **acts directly** — e.g. an agent comments on a GitHub issue or opens a PR via `gh`/`git` in its container; or
- **sends a new one-way message** on the appropriate thread, using the existing message-sending tools (`EscalateTool`, `RequestHelpTool`, `MessageRouterSkillInvoker`). That message is itself an event delivered to its recipient.

A "conversation" is therefore a sequence of one-way messages on a thread, each triggering a dispatch, each potentially emitting further messages. The **thread is the correlation primitive** — it already exists, is participant-set keyed, and is injected into prompt assembly as prior context. Request/reply, where genuinely needed, is a *pattern* built on top (send a message, end the turn, resume when the reply arrives on the thread) — never a transport primitive.

### 4. Control-plane queries keep synchronous reply

`MessageType.StatusQuery` and `HealthCheck` are **not** domain messages — they are infrastructure RPC: in-actor, immediate, no container. They keep their synchronous reply: `RouteAsync` returns the probe result `Message`. One-way delivery would buy nothing here and would make "what is your status?" absurd to consume. `RouteAsync` stays a single primitive whose `Message?` return is populated only for these control-plane queries and is a delivery ack (`null`) for every domain delivery.

### 5. Connectors are non-routable bridges with two surfaces

A connector is **never** a routable actor that receives domain messages or replies. It has exactly two surfaces:

1. **Inbound** — an event translator: external webhook → one-way domain message addressed at the bound unit.
2. **Outbound** — an agent-invoked **skill/tool**. GitHub already works this way (the agent runs `gh`/`git`; `LabelRoutingRoundtripSubscriber` applies writes off the activity bus). A future Slack connector exposes outbound capability as a `slack.send_message`-style tool — *not* a `connector://slack` message destination.

Consequences:

- **`ConnectorActor` and `IConnectorActor` are deleted.** They are a never-implemented shell (`HandleDomainMessageAsync` logs "event translation is not yet implemented" and returns an ack). No `message → connector` path exists or is planned.
- **The router's `connector://` proxy branch is deleted** (`AgentProxyResolver`), as is the worker's `RegisterActor<ConnectorActor>()`.
- **The synthetic `connector://…` `From` stays** — provenance and thread-participant keying only. With the reply hop removed, nothing ever attempts to route to it.

### 6. Units and agents are told the model

The platform system prompt (`PlatformPromptProvider`, Layer 1 of prompt assembly) gains a short paragraph: a message you receive is a notification that something happened; no caller is blocked waiting on your return value; act on it, and if a response or follow-up is warranted, send a new message on the thread or act through your tools — do not compose a reply to a caller.

### 7. Scope: producers in v0.1

Every domain producer already emits a one-way message; this ADR changes *consumption* (the coordinator stops routing the response), not production. So no producer needs new wiring. Future producers — time-based triggers, Slack-originating inbound messages — slot in as additional event sources with no contract change. v0.1 ships the one-way coordinator change and the connector cleanup.

## Consequences

- Webhook-originated dispatch responses are recorded on the thread instead of being dropped; `@savasp` can trace what the agent decided.
- A connector-origin response is no longer surfaced as an `ErrorOccurred` activity — the `#2548` error path is removed for the dispatch response (no routing hop remains to fail).
- `Message` is unchanged: no new field, no `[DataMember]` re-ordering, no migration of `new Message(...)` call sites. The change concentrates in `AgentDispatchCoordinator`.
- The `connector://` address scheme survives only as message provenance — no actor, no proxy branch, no registration.
- `RouteAsync` remains one method; callers must understand its `Message?` return is meaningful only for control-plane queries. Domain callers already treat it as `null`.
- Request/reply between agents, if a future flow needs it, is a thread-correlated pattern (send → end turn → resume on reply), not a transport feature. No v0.1 flow needs it.

## Alternatives considered

- **A `request`/`event` delivery axis on `Message`** (the first draft of this ADR). Rejected: it preserves a request/reply mechanism for domain messages that is already vestigial — the API returns a `null` domain payload today, and the only manufactured reply is the coordinator hop this ADR removes. The axis adds a new enum, a new `[DataMember]`, and a migration of every `new Message(...)` site to encode a distinction that does not pull its weight.
- **`MessageType.Event` as a sibling of `Domain`.** Rejected: every domain message is already an event; a separate type would force an `Event` case into every `Domain` switch for no behavioural gain.
- **Infer "do not reply" from `From.Scheme == connector`.** Rejected: a heuristic proxy that rots — a time-based trigger has no connector address. With domain messaging one-way, there is nothing to infer: nothing is ever routed back.
- **Reply to the orchestrating `unit://<id>`.** Rejected: units are agents (ADR-0039); the unit's own runtime produced the response, so routing it back re-enters the same actor.
- **A `connector://` short-circuit in `MessageRouter`.** Rejected: it would route the response into `ConnectorActor`, a shell that does nothing — silencing the warning while still dropping the response.
