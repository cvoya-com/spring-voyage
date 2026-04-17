// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Data;

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
        var turnName = nameof(ActivityEventType.ConversationStarted);
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
        // Until PR-PLAT-OBS-1 (#391) lands, the only wait-related signal on
        // the wire is the count of StateChanged events. We surface the count
        // as `StateTransitions` and leave the duration fields at zero so
        // downstream surfaces can render "no data yet" without a contract
        // change when the observability pipeline supplies durations.
        var stateChangedName = nameof(ActivityEventType.StateChanged);

        var query = dbContext.ActivityEvents
            .Where(e => e.EventType == stateChangedName)
            .Where(e => e.Timestamp >= from && e.Timestamp <= to);

        if (!string.IsNullOrEmpty(sourceFilter))
        {
            query = query.Where(e => e.Source.Contains(sourceFilter));
        }

        var entries = await query
            .GroupBy(e => e.Source)
            .Select(g => new WaitTimeEntry(
                g.Key,
                0d,
                0d,
                0d,
                g.LongCount()))
            .ToListAsync(cancellationToken);

        return new WaitTimeRollup(entries, from, to);
    }
}