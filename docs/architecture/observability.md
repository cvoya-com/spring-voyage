# Observability

> **[Architecture Index](README.md)** | Related: [Infrastructure](infrastructure.md), [Units & Agents](units.md), [Initiative](initiative.md)

---

## Structured Activity Events

Observability is a first-class architectural concern, not an afterthought.

Every `IActivityObservable` entity emits typed events via `IObservable<ActivityEvent>`:

```
ActivityEvent:
  timestamp: DateTimeOffset
  source: Address
  type: enum (MessageReceived, MessageSent, ConversationStarted, ConversationCompleted,
              DecisionMade, ErrorOccurred, StateChanged, InitiativeTriggered,
              ReflectionCompleted, WorkflowStepCompleted, CostIncurred,
              TokenDelta, ...)
  severity: enum (Debug, Info, Warning, Error)
  summary: string                    # human-readable one-liner
  details: JsonElement               # structured payload
  correlation_id: string             # traces related events
  cost: decimal?                     # LLM cost if applicable
```

## Rx.NET for Stream Processing

Using `IObservable<ActivityEvent>` with Rx.NET 6.x enables powerful real-time processing:

```csharp
// Batched UI updates (1-second windows)
agent.ActivityStream
    .Buffer(TimeSpan.FromSeconds(1))
    .Subscribe(batch => dashboard.Update(batch));

// Alert on errors only
agent.ActivityStream
    .Where(e => e.Severity >= Severity.Warning)
    .Subscribe(e => alertService.Notify(e));

// Merge multiple agent streams for a unit dashboard
Observable.Merge(unit.Members.Select(m => m.ActivityStream))
    .Subscribe(e => unitDashboard.Update(e));

// Cost tracking with windowed aggregation
agent.ActivityStream
    .Where(e => e.Cost.HasValue)
    .Window(TimeSpan.FromHours(1))
    .SelectMany(w => w.Sum(e => e.Cost!.Value))
    .Subscribe(hourlyCost => costTracker.Record(hourlyCost));
```

## Observation Layers


| Layer                     | What                          | How                          |
| ------------------------- | ----------------------------- | ---------------------------- |
| **Agent → Agent**         | Mentoring, quality monitoring | Pub/sub with permission      |
| **Unit → Members**        | Orchestration awareness       | Implicit (unit sees members) |
| **Human → Agent/Unit**    | Dashboard, CLI, alerts        | SSE/WebSocket + REST         |
| **Platform → Everything** | Telemetry, cost, audit        | System-wide collection       |


## Cost Tracking

Every LLM call tracks cost. Roll-ups at agent, unit, and tenant level:

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

- **SSE/WebSocket** — real-time streaming to web dashboard
- **Pub/Sub Topics** — agent-to-agent observation
- **Persistent Store** — all events stored for replay and analytics
- **Notifications** — Slack, email, GitHub comments (via connectors)

> **Open issue: Event stream separation.** Currently, `ActivityEvent` covers both high-frequency execution events (`TokenDelta`) and higher-level activity events (`ConversationStarted`, `DecisionMade`). A single type simplifies the model and Rx.NET filtering handles volume. However, for very active agents the high-frequency token stream may overwhelm consumers interested only in summaries. A future revision may separate these into two streams: a high-frequency execution stream and a lower-frequency activity stream.

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
