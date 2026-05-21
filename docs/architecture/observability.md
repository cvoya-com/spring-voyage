# Observability

> **[Architecture Index](README.md)** | Related: [Infrastructure](infrastructure.md), [Units](units.md), [Agents](agents.md), [Initiative](initiative.md)

---

## Structured Activity Events

Observability is a first-class architectural concern, not an afterthought.

Every `IActivityObservable` entity emits typed events via `IObservable<ActivityEvent>`:

```
ActivityEvent:
  timestamp: DateTimeOffset
  source: Address
  type: enum (MessageReceived, MessageSent, ThreadStarted,
              DecisionMade, ErrorOccurred, StateChanged, InitiativeTriggered,
              ReflectionCompleted, WorkflowStepCompleted, CostIncurred,
              TokenDelta, ReflectionActionDispatched, ReflectionActionSkipped,
              AmendmentReceived, AmendmentRejected, ToolCall, ToolResult)
  severity: enum (Debug, Info, Warning, Error)
  summary: string                    # human-readable one-liner
  details: JsonElement               # structured payload
  correlation_id: string             # traces related events
  cost: decimal?                     # LLM cost if applicable
```

## Rx.NET Topology — end-to-end reactive pipeline

The platform uses a **single process-wide hot bus** (`IActivityEventBus`) as the backbone for every observability consumer. Every producer publishes to it; every consumer subscribes with Rx.NET operators to compose the view it needs. There is no second mechanism (no polling loop, no separate pub/sub fan-out inside a host).

```
                 ┌─────────────────────────────────────────────────────┐
                 │             IActivityEventBus (Subject<T>)          │
                 └─────────────────────────────────────────────────────┘
                    ▲                ▲                ▲           ▲
   emit (in-proc)   │                │                │           │ subscribe (Rx)
  ─────────────────┬┼───────────────┬┼───────────────┬┼──────────┬┘
                   │                │                │           │
 AgentActor        │  UnitActor     │  HumanActor    │  Stream   │  SSE /api/v1/activity/stream
  MessageReceived  │   DecisionMade │  MessageRcvd   │  Event    │    Per-source permission filter
  ThreadStarted    │   StateChanged │                │  Sub-     │    Permission-at-subscribe for
  DecisionMade     │   MemberChange │                │  scriber  │    unit-scoped (?unitId=X)
  ErrorOccurred    │   ErrorOccur'd │                │  (Dapr    │    Bounded channel back-pressure
  StateChanged     │                │                │   pub/sub)│
  CostIncurred     │                │                │           │  CostTracker (Buffer 1s) ──►  CostRecord EF
  AmendmentRcvd    │                │                │  TokenDelta  BudgetEnforcer    ──►  InitiativePaused state
  AmendmentReject'd│                │                │  ToolCall    ActivityEventPersister  (Buffer 1s) ──► ActivityEventRecord EF
  RefActnDispatch'd│                │                │  ToolResult  IUnitActivityObservable  (Observable.Merge of members)
  RefActnSkipped   │                │                │  Completed   IConversationQueryService
  TokenDelta       │                │                │
  ToolCall         │                │                │
  ToolResult       │                │                │
  InitiativeTrig'd │                │                │
  ReflectionComp'd │                │                │
```

Consumers compose Rx.NET operators on `ActivityStream`:

```csharp
// Batched UI updates (1-second windows) — dashboards, persistence, cost aggregation
bus.ActivityStream
    .Buffer(TimeSpan.FromSeconds(1))
    .Subscribe(batch => dashboard.Update(batch));

// Alert on errors only — route to ops channels / Slack via connectors
bus.ActivityStream
    .Where(e => e.Severity >= ActivitySeverity.Warning)
    .Subscribe(e => alertService.Notify(e));

// Merge every member's stream for a unit dashboard
// (Encapsulated by IUnitActivityObservable — see below)
unitObservable.GetStreamAsync(unitId)
    .Subscribe(e => unitDashboard.Update(e));

// Windowed cost tracking
bus.ActivityStream
    .Where(e => e.EventType == ActivityEventType.CostIncurred)
    .Buffer(TimeSpan.FromSeconds(1))
    .Subscribe(batch => costRepo.Append(batch));
```

### Emission sites — every event type reaches subscribers

| Source | Event types emitted | Where |
|--------|--------------------|-----|
| `AgentActor.ReceiveAsync` | `MessageReceived` | every message, carrying `conversationId` as `CorrelationId` |
| `AgentActor.HandleDomainMessageAsync` | `ThreadStarted`, `StateChanged (Idle→Active)`, `DecisionMade` | new conversation, queued conversation, membership-disabled / unit-policy blocks |
| `AgentActor.HandleCancelAsync` | (no events) | per-thread cancel cleanly tears down the matched thread's channel and dispatcher CTS; no thread-level activity event is emitted (#2076 / ADR-0030 §3) |
| `AgentActor.HandleAmendmentAsync` | `AmendmentReceived`, `AmendmentRejected`, `StateChanged (Active→Paused)` | supervisor amendments (#142) |
| `AgentActor.SetMetadataAsync / SetSkillsAsync / ClearParentUnitAsync` | `StateChanged` | configuration edits |
| `AgentActor.RunDispatchAsync` | `ErrorOccurred` | dispatcher failures |
| `AgentActor.EmitCostIncurredAsync` | `CostIncurred` | every LLM completion, carries `Cost`, `model`, `inputTokens`, `outputTokens`, `costSource` |
| `AgentActor.RunInitiativeCheckAsync` | `InitiativeTriggered`, `ReflectionCompleted`, `ReflectionActionDispatched`, `ReflectionActionSkipped` | Tier-2 reflection loop |
| `UnitActor.ReceiveAsync / HandleDomainMessageAsync` | `MessageReceived`, `DecisionMade`, `ErrorOccurred`, `WorkflowStepCompleted` | runtime dispatch, orchestration delegation, dispatcher no-response completions |
| `AgentDispatchCoordinator.RunDispatchAsync` | `DecisionMade`, `WorkflowStepCompleted`, `ErrorOccurred` | for a **connector-origin** turn (`From.Scheme == connector`), emits a `DecisionMade` recording the routing **outcome** after the runtime invocation returns — `event_processed` / `processing_failed` — so the activity stream is never silent on a connector event, **including the "no agent dispatched" case** (#2560). `Details` carries `decision`, `connectorEventType`, the external entity reference (e.g. issue number), and `inboundMessageId`; `CorrelationId` is the originating connector thread id. The runtime-facing signal that would let this event carry the unit's actual decision + `dispatched_to` is deferred design work (#2572) |
| `MessagingToolHandlers.HandleSendAsync / HandleMulticastAsync` | `MessageSent` | an `sv.messaging.send` / `sv.messaging.multicast` from a unit's runtime — carries the target(s); `CorrelationId` is the thread id, so it joins the same correlation chain as the connector `MessageReceived` and the coordinator's routing-outcome `DecisionMade` (#2560). A routing decision, when the runtime chooses to record one, is a separate optional `sv.runtime.report_decision` call that emits `DecisionMade` |
| `UnitActor.AddMemberAsync / RemoveMemberAsync / TransitionAsync / SetMetadataAsync` | `StateChanged` | membership, lifecycle, metadata edits |
| `UnitEndpoints` force-delete | `StateChanged` | force-delete audit |
| `HumanActor.ReceiveAsync` | `MessageReceived` | human inbox (#456) |
| `StreamEventSubscriber` (Dapr pub/sub) | `TokenDelta`, `ToolCall`, `ToolResult`, `StateChanged` | bridges execution-environment events into the activity bus; failing tool results escalate to `Warning` |
| `BudgetEnforcer` | `CostIncurred` (synthetic warning/error) | budget threshold hits |

### Subscribers

| Subscriber | What it does | Operators |
|-----------|-------------|-----------|
| `ActivityEventPersister` | Persists every event | `Buffer(1s)` → EF `SaveChangesAsync` |
| `CostTracker` | Per-agent cost rollups | `Where(CostIncurred)`.`Buffer(1s)` → `CostRecord` |
| `BudgetEnforcer` | Budget thresholds, pause-initiative | `Where(CostIncurred)` |
| `IUnitActivityObservable` | Unit-scoped stream — merge of member events | filter closure over the member set at subscribe time |
| SSE relay `/api/v1/activity/stream` | Live dashboards | per-source permission cache or unit-scoped permission gate; bounded channel back-pressure |
| `IConversationQueryService` | Conversation projection for inbox | materialised from the activity event table |

### Permission contract — checked at subscribe time

The SSE endpoint `/api/v1/activity/stream` supports two shapes:

1. **Unit-scoped** (`?unitId=…`): the caller's `IPermissionService.ResolvePermissionAsync(humanId, unitId)` is resolved **once before the stream opens**. Callers with no permission or below `Viewer` get `403 Forbidden`. Once authorised, the relay subscribes to `IUnitActivityObservable.GetStreamAsync(unitId)`, which walks the unit's member graph at subscribe time and returns a filter over the platform bus restricted to that address set. The permission check never runs per event on this path.

2. **Platform-wide** (no `unitId`): events flow from every source. Per-source permission is resolved lazily via a concurrent cache keyed by `(humanId, unitId)` for the lifetime of the subscription — a `unit:`-sourced event is dropped for unauthorised subscribers, everything else passes. Agent, human, and tenant sources don't require unit-level permission because their containing unit's authorisation is what the caller lacks (and if the caller holds permission on a descendant unit, they'll see those events through a unit-scoped subscription).

Permission is never re-resolved per event on the hot path by actor proxy calls: the cache guarantees at-most-one actor roundtrip per unique source per subscription.

### Back-pressure

The SSE relay decouples Rx.NET's synchronous `OnNext` callback from the HTTP writer via a **bounded channel** (`Channel.CreateBounded<ActivityEvent>(256, DropOldest)`). The Rx subscription writes into the channel without ever blocking the producer; a single writer loop drains the channel into the response body. A disconnected client trips `OperationCanceledException` in the writer, which completes the channel and disposes the subscription. Worst-case bursts (e.g., a chatty `TokenDelta` stream) drop the oldest events on the floor rather than queuing unboundedly or blocking the actor that emitted them.

## Observation Layers


| Layer                     | What                          | How                          |
| ------------------------- | ----------------------------- | ---------------------------- |
| **Agent → Agent**         | Mentoring, quality monitoring | Pub/sub with permission      |
| **Unit → Members**        | Orchestration awareness       | `IUnitActivityObservable.GetStreamAsync` — subscribe-time filter over the bus |
| **Human → Agent/Unit**    | Dashboard, CLI, alerts        | SSE/WebSocket + REST         |
| **Platform → Everything** | Telemetry, cost, audit        | System-wide collection       |


## Cost Tracking

Every LLM call tracks cost. Roll-ups at agent, unit, and tenant level are materialised by `CostTracker` from `CostIncurred` activity events; there is no separate cost-bus and no polling.

```
Cost Tracking:
  per_call:   { model, tokens_in, tokens_out, cost, duration }
  per_agent:  { total_cost_today, total_cost_month, initiative_cost, work_cost }
  per_unit:   { total_cost, cost_by_agent, cost_by_activity_type }
Alerts:
  - Agent exceeds daily budget → pause initiative
  - Unit exceeds monthly budget → notify owner
  - Unusual cost spike → alert admin
```

## Delivery Channels

- **SSE/WebSocket** — real-time streaming to web dashboard (`/api/v1/activity/stream`)
- **Pub/Sub Topics** — execution-environment stream events (TokenDelta, ToolCall, ToolResult, Completed) flow over Dapr pub/sub into the in-process bus via `StreamEventSubscriber`
- **Persistent Store** — all events stored for replay and analytics (`ActivityEventPersister`)
- **Notifications** — Slack, email, GitHub comments (via connectors)

## Runtime activity capture — OTLP/HTTP plane (#2492)

Runtime containers (agent, unit, sub-agents) ship spans, logs, and span events to the platform over **OTLP/HTTP+JSON** at `/otlp/v1/traces` and `/otlp/v1/logs`. The launcher (`SpringVoyageAgentLauncher`) injects the canonical OTel env vars:

- `OTEL_EXPORTER_OTLP_ENDPOINT` — `https://<platform>/otlp`
- `OTEL_EXPORTER_OTLP_PROTOCOL=http/json`
- `OTEL_EXPORTER_OTLP_HEADERS=Authorization=Bearer <per-invocation callback JWT>`
- `OTEL_RESOURCE_ATTRIBUTES=sv.tenant.id=...,sv.subject.uuid=...,sv.subject.kind=agent|unit|human`

Auth reuses the per-invocation callback token the launcher already mints for the dispatcher orchestration callback — no new credential primitive. The ingest controller cross-checks the resource attributes against the bearer JWT's claims so a leaked token can't be replayed against another subject; mismatches drop silently.

### Capture, redaction, retention

A tenant-scoped setting (`tenant_activity_settings.level`) gates the ingest at three levels — `off`, `summary`, `full`. OSS default is `full`; truncation at `summary` keeps the head and tail of every long string (>1024 chars) and marks the parent object with `truncated: true`. Library-defined redaction (`ActivityRedactor`) masks the well-known auth-header keys (`Authorization`, `Proxy-Authorization`, `X-API-Key`, `X-Auth-Token`, `Cookie`, `Set-Cookie`) and env-var keys matching `*_TOKEN` / `*_KEY` / `*_SECRET` / `*_PASSWORD`; in-line bearer/basic tokens in attribute values are scrubbed. Redaction runs **before** capture-level truncation so the masked marker stays intact through summary-level trimming.

Per-tenant retention (`tenant_activity_settings.retention_days`, default 30 days) drives `ActivityRetentionPurgeService` — a daily background sweep that deletes `activity_events` rows older than the horizon. The sweep opens an `ITenantScopeBypass` scope so the cross-tenant query is auditable.

### Best-effort capture

A broken collector path must NOT block the A2A request/response path. The ingest service catches every publish failure, increments a per-batch `DroppedError` counter on the OTLP response, and lets the runtime carry on. The portal's existing Activity tab (`/api/v1/tenant/activity/stream` SSE) consumes the new event types (`RuntimeSpan`, `RuntimeLog`, `RuntimeProgress`, `LlmTurn`, `ToolCall`) identically to existing event types — the underlying bus is unchanged.

### CLI live-tail

`spring agent tail <id>`, `spring unit tail <id>`, `spring human tail <id>`, and `spring activity tail` all subscribe to the same SSE stream through the generated Kiota client (no raw HttpClient). Filter flags: `--thread`, `--message`, `--kind` (repeatable), `--from`, `--severity`. `--json` switches the output to one JSON object per event for `jq` consumers.

### Out of scope (follow-ups)

- **OTLP/protobuf encoding** — v0.1 ships JSON only. Adding protobuf is additive — runtimes simply set `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` and the ingest controller learns the new content type.
- **Forwarding to a tenant-configured external OTel backend** (Datadog, Tempo, Jaeger). The OSS surface stays as a sink; the cloud overlay registers a decorating `IOtlpIngestService` to fan out.
- **Tenant-defined PII redaction rules.** Only the library-defined match list ships in v0.1.
- **Token-by-token LLM streaming.** Full-turn capture only via `sv.llm.turn` spans.

> **Open issue: Event stream separation.** Currently, `ActivityEvent` covers both high-frequency execution events (`TokenDelta`, `ToolCall`, `ToolResult`) and higher-level activity events (`ThreadStarted`, `DecisionMade`). A single type simplifies the model and Rx.NET filtering handles volume. However, for very active agents the high-frequency stream may overwhelm consumers interested only in summaries. A future revision may separate these into two streams: a high-frequency execution stream and a lower-frequency activity stream.

---

## Cost Model

### Per-Agent Daily Cost


| Component                         | Passive    | Attentive  | Proactive  |
| --------------------------------- | ---------- | ---------- | ---------- |
| Active work (8 conversations/day) | ~$8-15     | ~$8-15     | ~$8-15     |
| Initiative screening (Tier 1)     | $0         | ~$0        | ~$0        |
| Initiative reflection (Tier 2)    | $0         | ~$0.20     | ~$0.50     |
| Memory/expertise                  | ~$0        | ~$0.10     | ~$0.20     |
| **Daily total**                   | **~$8-15** | **~$8-15** | **~$9-16** |


### Per-Unit Monthly (10 agents, proactive)


| Component           | Cost              |
| ------------------- | ----------------- |
| Agent work          | ~$2,400-4,500     |
| Initiative overhead | ~$150-200         |
| Tier 1 LLM hosting  | ~$20-50           |
| Infrastructure      | ~$50-100          |
| **Monthly total**   | **~$2,600-4,850** |


Initiative adds ~6-8% to total cost while enabling proactive value.
