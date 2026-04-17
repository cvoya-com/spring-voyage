# 0009 — GitHub label roundtrip wired via activity-event subscription

- **Status:** Accepted — strategy emits `DecisionMade` event; GitHub connector subscribes as a hosted service.
- **Date:** 2026-04-16
- **Closes:** [#492](https://github.com/cvoya-com/spring-voyage/issues/492)
- **Related code:** `src/Cvoya.Spring.Dapr/Orchestration/LabelRoutedOrchestrationStrategy.cs`, `src/Cvoya.Spring.Connector.GitHub/Labels/LabelRoutingRoundtripSubscriber.cs`, `src/Cvoya.Spring.Connector.GitHub/DependencyInjection/ServiceCollectionExtensions.cs`

## Context

ADR-0007 landed `LabelRoutingPolicy` with `AddOnAssign` and `RemoveOnAssign` fields plus a `LabelRoutedOrchestrationStrategy` that routes inbound labelled messages to the matching member. The strategy deliberately does **not** perform the label write because only the GitHub connector holds the credentials needed to mutate remote repository state. #492 is the follow-up that closes the gap: after a label-routed assignment, the GitHub connector must add the `AddOnAssign` labels and strip the `RemoveOnAssign` labels on the originating issue.

Three wiring options were on the table:

1. **Connector subscribes to a new activity event emitted by the strategy.**
2. **New hook on `IUnitConnector`/`IConnectorType` invoked by `UnitActor`** after orchestration dispatches.
3. **Direct call from the strategy into an `ILabelRoundtripSink` injected via DI**, with the GitHub connector registering the concrete sink.

## Decision

**Option 1: the strategy publishes a `DecisionMade` activity event carrying the roundtrip coordinates; the GitHub connector subscribes to the platform `IActivityEventBus` via a hosted service and applies the roundtrip.**

### Event shape

On a successful forward, `LabelRoutedOrchestrationStrategy` publishes:

```
ActivityEvent {
  EventType: DecisionMade,
  Severity:  Info,
  Source:    context.UnitAddress,           // unit://engineering-team
  Details: {
    decision:        "LabelRouted",
    unitAddress:     { scheme, path },
    matchedLabel:    "agent:backend",
    target:          { scheme: "agent", path: "backend-engineer" },
    source:          "github" | ...,         // from payload
    repository:      { owner, name } | null, // from payload
    issue:           { number } | null,      // from payload
    addOnAssign:     [ "in-progress" ],
    removeOnAssign:  [ "agent:backend" ],
    messageId:       <guid>,
  }
}
```

The marker string `"LabelRouted"` is exported as `LabelRoutedOrchestrationStrategy.LabelRoutedDecision` so subscribers filter on a constant, not a stringly-typed literal.

### Subscription

`LabelRoutingRoundtripSubscriber` (new, in `Cvoya.Spring.Connector.GitHub.Labels`) is registered as an `IHostedService`. On `StartAsync` it subscribes to `IActivityEventBus.ActivityStream` with `Where(IsLabelRoutedGitHubAssignment)` (filters by `EventType == DecisionMade`, `details.decision == "LabelRouted"`, and `details.source == "github"`). For each matching event it mints an authenticated client via the existing `IGitHubConnector.CreateAuthenticatedClientAsync`, then calls Octokit's `Issue.Labels.RemoveFromIssue` / `AddToIssue`.

### Idempotency and failure handling

- Removing a label that is not on the issue: GitHub returns 404; the subscriber swallows `NotFoundException` as a no-op and continues.
- Adding labels: GitHub tolerates duplicates server-side; a single batched `AddToIssue` call handles the full list.
- Permission errors (403 / 401) abort the roundtrip and log a warning; the subscription stays live for subsequent events.
- Any other exception in the Rx `OnNext` handler is caught, logged, and swallowed. The Rx subscription itself never tears down because of a handler fault — subsequent events must continue to flow.

## Alternatives considered and rejected

- **New `IConnectorType.OnAssignmentAsync` hook.** Tempting because `IConnectorType` already has `OnUnitStarting/Stopping`. Rejected: it couples the orchestration strategy to the connector-type lifecycle surface and pushes strategy-aware logic into `UnitActor`. It would also require every strategy that wants the hook to call into it explicitly — a capability the activity-event model already provides for free. The connector remains a generic observer; the strategy remains a generic emitter.
- **Direct `ILabelRoundtripSink` dependency.** Clean, but introduces a Core interface whose only consumer is GitHub today. The activity-event model is already the platform's standard cross-cutting mechanism for "something happened; possibly many components care" (see ADR-0008 context: cost tracking, persistence, SSE relay all subscribe to the same bus). Minting a second mechanism for the first label-aware connector would calcify a wrong shape.
- **Polling the orchestration-decision table.** The activity-event bus is already in-process hot; persistence is a subscriber. A poll-based path would invert that flow for no benefit.

## Consequences

- **Open extension to other label-aware connectors.** A future Linear or Jira connector can subscribe to the same bus, filter on `details.source == "linear"`, and apply its own roundtrip without touching core. The `source` field is the only coupling point.
- **Best-effort semantics.** The roundtrip is not atomic with the message forward — a crash between `SendAsync` returning and the subscriber firing loses the label write. This is acceptable for v1: the webhook that triggered the label event is itself retried by GitHub, and a second-pass assignment sees the labels still in place and re-applies the same roundtrip idempotently.
- **No order guarantees across subscribers.** If a future subscriber (e.g. metrics, audit) also observes `DecisionMade`, it runs in parallel with the roundtrip. That matches existing subscriber semantics.
- **`Microsoft.Extensions.Hosting` + `System.Reactive` pulled into the GitHub connector package.** Both are already transitive dependencies of every host that registers the connector, so the added surface area is zero at the host level; the package references simply make the build-time dependencies explicit.
- **Strategy emission is best-effort.** A fault on `IActivityEventBus.PublishAsync` is logged and swallowed — the routing decision is the primary artefact and a broken observer must not fault the orchestration turn.

## Revisit criteria

- **Durability becomes a requirement.** If operators report lost roundtrips after host crashes, move the subscription behind an outbox (publish to durable Dapr pub/sub and subscribe from a worker) rather than the in-process `IActivityEventBus`.
- **A second label-aware connector lands.** Extract the shared filter/extract helpers out of `LabelRoutingRoundtripSubscriber` into a per-connector base when the third implementation copy-pastes the same JSON-pluck.
- **Strategy starts emitting more structured events.** If we grow a dedicated `ActivityEventType.LabelRoutedAssignment` (instead of reusing `DecisionMade` with a discriminator), simplify the subscriber's filter to a single enum check.
