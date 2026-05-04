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
  type: enum (MessageReceived, MessageSent, ThreadStarted, ThreadCompleted,
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
  ThreadStart/End  │   StateChanged │                │  Sub-     │    Permission-at-subscribe for
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
| `AgentActor.HandleCancelAsync` | `ThreadCompleted`, `StateChanged (Active→Idle)` | cancel path |
| `AgentActor.HandleAmendmentAsync` | `AmendmentReceived`, `AmendmentRejected`, `StateChanged (Active→Paused)` | supervisor amendments (#142) |
| `AgentActor.SetMetadataAsync / SetSkillsAsync / ClearParentUnitAsync` | `StateChanged` | configuration edits |
| `AgentActor.RunDispatchAsync` | `ErrorOccurred` | dispatcher failures |
| `AgentActor.EmitCostIncurredAsync` | `CostIncurred` | every LLM completion, carries `Cost`, `model`, `inputTokens`, `outputTokens`, `costSource` |
| `AgentActor.RunInitiativeCheckAsync` | `InitiativeTriggered`, `ReflectionCompleted`, `ReflectionActionDispatched`, `ReflectionActionSkipped` | Tier-2 reflection loop |
| `UnitActor.ReceiveAsync / HandleDomainMessageAsync` | `MessageReceived`, `DecisionMade`, `ErrorOccurred` | orchestration delegation |
| `UnitActor.AddMemberAsync / RemoveMemberAsync / TransitionAsync / SetMetadataAsync` | `StateChanged` | membership, lifecycle, metadata edits |
| `UnitEndpoints` force-delete | `StateChanged` | force-delete audit |
| `HumanActor.ReceiveAsync` | `MessageReceived` | human inbox (#456) |
| `StreamEventSubscriber` (Dapr pub/sub) | `TokenDelta`, `ToolCall`, `ToolResult`, `ThreadCompleted`, `StateChanged` | bridges execution-environment events into the activity bus; failing tool results escalate to `Warning` |
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
