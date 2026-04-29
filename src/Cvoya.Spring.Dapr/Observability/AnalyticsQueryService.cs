// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF-Core-backed implementation of <see cref="IAnalyticsQueryService"/>
/// (PR-C3 / #457). Aggregates rollups over the persisted
/// <c>ActivityEvents</c> table so the CLI's <c>spring analytics</c> verbs
/// and the portal's Analytics surface share one query source of truth.
/// </summary>
public class AnalyticsQueryService(SpringDbContext dbContext) : IAnalyticsQueryService
{
    /// <inheritdoc />
    public async Task<ThroughputRollup> GetThroughputAsync(
        string? sourceFilter,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        // Throughput queries the same event stream as the activity endpoint
        // (the portal's Throughput tab and CLI's `spring analytics throughput`
        // both target this), so the filter and window semantics are identical
        // to ActivityQueryService.QueryAsync for observability cross-checks.
        var query = dbContext.ActivityEvents
            .Where(e => e.Timestamp >= from && e.Timestamp <= to);

        if (!string.IsNullOrEmpty(sourceFilter))
        {
            query = query.Where(e => e.Source.Contains(sourceFilter));
        }

        // EF translates nameof(...) constants for enum-typed event strings
        // into plain equality filters, so we compute the string names once
        // rather than calling .ToString() inside the LINQ expression.
        var receivedName = nameof(ActivityEventType.MessageReceived);
        var sentName = nameof(ActivityEventType.MessageSent);
        var turnName = nameof(ActivityEventType.ThreadStarted);
        var toolCallName = nameof(ActivityEventType.DecisionMade);

        var grouped = await query
            .GroupBy(e => e.Source)
            .Select(g => new ThroughputEntry(
                g.Key,
                g.Count(e => e.EventType == receivedName),
                g.Count(e => e.EventType == sentName),
                g.Count(e => e.EventType == turnName),
                g.Count(e => e.EventType == toolCallName)))
            .ToListAsync(cancellationToken);

        return new ThroughputRollup(grouped, from, to);
    }

    /// <inheritdoc />
    public async Task<WaitTimeRollup> GetWaitTimesAsync(
        string? sourceFilter,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        // PR-PLAT-OBS-1 (#391) landed the Rx activity pipeline that carries
        // lifecycle-state transitions through the bus and into the
        // ActivityEvents table. Each canonical lifecycle StateChanged event
        // (Idle⇄Active, Active→Paused, Active→Suspended — see
        // docs/architecture/observability.md) carries a `{ from, to }`
        // payload in `Details`. We pair consecutive events per source and
        // accumulate time-in-state into the three buckets the
        // `WaitTimeEntry` contract exposes:
        //
        //   - `from = "Idle"`   → IdleSeconds
        //   - `from = "Active"` → BusySeconds
        //   - `from = "Paused"` → WaitingForHumanSeconds
        //
        // Metadata-edit StateChanged events (which don't carry `from`/`to`)
        // still count toward `StateTransitions` but don't contribute to any
        // duration bucket. A span that's still open at the end of the window
        // is clamped to the window's `to` bound so the reported durations are
        // always bounded by the window.
        var stateChangedName = nameof(ActivityEventType.StateChanged);

        var query = dbContext.ActivityEvents
            .Where(e => e.EventType == stateChangedName)
            .Where(e => e.Timestamp >= from && e.Timestamp <= to);

        if (!string.IsNullOrEmpty(sourceFilter))
        {
            query = query.Where(e => e.Source.Contains(sourceFilter));
        }

        // Pull the events into memory: we need to parse the JSON `Details`
        // payload to recover the `from`/`to` state, which a server-side
        // GROUP BY can't do portably across providers. The result set is
        // bounded by the window, and this service is used for analytics
        // surfaces (not the hot path) so the materialisation cost is
        // acceptable.
        var rawEvents = await query
            .OrderBy(e => e.Source)
            .ThenBy(e => e.Timestamp)
            .Select(e => new { e.Source, e.Timestamp, e.Details })
            .ToListAsync(cancellationToken);

        var entries = rawEvents
            .GroupBy(e => e.Source)
            .Select(g => ComputeEntry(
                g.Key,
                g.Select(e => new StateChangedEvent(e.Source, e.Timestamp, e.Details)).ToList(),
                to))
            .ToList();

        return new WaitTimeRollup(entries, from, to);
    }

    /// <summary>
    /// Computes an aggregated <see cref="WaitTimeEntry"/> for one source by
    /// pairing consecutive canonical lifecycle transitions and accumulating
    /// time-in-state into idle / busy / waiting-for-human buckets. See the
    /// block comment in <see cref="GetWaitTimesAsync"/> for the contract.
    /// </summary>
    private static WaitTimeEntry ComputeEntry(
        string source,
        IReadOnlyList<StateChangedEvent> orderedEvents,
        DateTimeOffset windowEnd)
    {
        double idle = 0d;
        double busy = 0d;
        double waitingForHuman = 0d;

        // Walk canonical lifecycle transitions in order. The `to` state of
        // event[i] is the agent's state until event[i+1] (or until the window
        // end for the final canonical event). Non-canonical events (metadata
        // edits — no `from`/`to` in Details) are ignored for duration
        // attribution but still count toward StateTransitions below.
        DateTimeOffset? openSpanStart = null;
        string? openSpanState = null;
        foreach (var evt in orderedEvents)
        {
            var fromState = TryReadState(evt.Details, "from");
            var toState = TryReadState(evt.Details, "to");

            // Only events carrying both `from` and `to` are canonical lifecycle
            // transitions; metadata-edit StateChanged payloads don't.
            if (fromState is null || toState is null)
            {
                continue;
            }

            if (openSpanStart is not null && openSpanState is not null)
            {
                AccumulateBucket(openSpanState, openSpanStart.Value, evt.Timestamp,
                    ref idle, ref busy, ref waitingForHuman);
            }

            openSpanStart = evt.Timestamp;
            openSpanState = toState;
        }

        // The final span is still open — clamp to the window end so we don't
        // over-count. (If the span extends past the window, the next query
        // call that includes the closing transition will see it via its own
        // events.)
        if (openSpanStart is not null && openSpanState is not null)
        {
            AccumulateBucket(openSpanState, openSpanStart.Value, windowEnd,
                ref idle, ref busy, ref waitingForHuman);
        }

        return new WaitTimeEntry(source, idle, busy, waitingForHuman, orderedEvents.Count);
    }

    private static void AccumulateBucket(
        string state,
        DateTimeOffset start,
        DateTimeOffset end,
        ref double idle,
        ref double busy,
        ref double waitingForHuman)
    {
        if (end <= start)
        {
            return;
        }

        var seconds = (end - start).TotalSeconds;

        // Map the `to` state of the prior transition onto the three buckets
        // the WaitTimeEntry contract exposes. Suspended is a valid lifecycle
        // state but is not one of the three exposed buckets — drop it rather
        // than silently mis-attribute. See #476.
        switch (state)
        {
            case "Idle":
                idle += seconds;
                break;
            case "Active":
                busy += seconds;
                break;
            case "Paused":
                waitingForHuman += seconds;
                break;
        }
    }

    private static string? TryReadState(JsonElement? details, string propertyName)
    {
        if (details is null || details.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (details.Value.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    /// <summary>
    /// Per-source projection used by <see cref="GetWaitTimesAsync"/> while
    /// walking events. Mirrors the subset of <see cref="ActivityEventRecord"/>
    /// the in-memory duration accumulator needs.
    /// </summary>
    private sealed record StateChangedEvent(
        string Source,
        DateTimeOffset Timestamp,
        JsonElement? Details);
}