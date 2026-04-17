// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Maps analytics API endpoints for throughput and wait-time rollups — the
/// portal's Analytics surface (#448) and `spring analytics` CLI verbs (#457)
/// read through here. Costs keep their own endpoints (<see cref="CostEndpoints"/>)
/// and the analytics CLI's <c>costs</c> verb aliases those directly.
/// </summary>
public static class AnalyticsEndpoints
{
    /// <summary>
    /// Registers analytics endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/analytics")
            .WithTags("Analytics");

        group.MapGet("/throughput", GetThroughputAsync)
            .WithName("GetAnalyticsThroughput")
            .WithSummary("Get throughput counters (messages / turns / tool calls) per source over a time range")
            .Produces<ThroughputRollupResponse>(StatusCodes.Status200OK);

        group.MapGet("/waits", GetWaitTimesAsync)
            .WithName("GetAnalyticsWaits")
            .WithSummary("Get wait-time rollups per source; duration fields are zero-filled until the observability pipeline (PR-PLAT-OBS-1) supplies them")
            .Produces<WaitTimeRollupResponse>(StatusCodes.Status200OK);

        return group;
    }

    private static async Task<IResult> GetThroughputAsync(
        IAnalyticsQueryService analyticsQueryService,
        string? source,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var (rangeFrom, rangeTo) = ResolveTimeRange(from, to);
        var rollup = await analyticsQueryService.GetThroughputAsync(source, rangeFrom, rangeTo, cancellationToken);
        return Results.Ok(ToResponse(rollup));
    }

    private static async Task<IResult> GetWaitTimesAsync(
        IAnalyticsQueryService analyticsQueryService,
        string? source,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var (rangeFrom, rangeTo) = ResolveTimeRange(from, to);
        var rollup = await analyticsQueryService.GetWaitTimesAsync(source, rangeFrom, rangeTo, cancellationToken);
        return Results.Ok(ToResponse(rollup));
    }

    // Mirrors the default used by CostEndpoints so the three analytics verbs
    // (costs/throughput/waits) share a single "no --from/--to → last 30 days"
    // convention.
    private static (DateTimeOffset From, DateTimeOffset To) ResolveTimeRange(DateTimeOffset? from, DateTimeOffset? to)
    {
        var rangeTo = to ?? DateTimeOffset.UtcNow;
        var rangeFrom = from ?? rangeTo.AddDays(-30);
        return (rangeFrom, rangeTo);
    }

    private static ThroughputRollupResponse ToResponse(ThroughputRollup rollup) =>
        new(
            rollup.Entries
                .Select(e => new ThroughputEntryResponse(
                    e.Source, e.MessagesReceived, e.MessagesSent, e.Turns, e.ToolCalls))
                .ToList(),
            rollup.From,
            rollup.To);

    private static WaitTimeRollupResponse ToResponse(WaitTimeRollup rollup) =>
        new(
            rollup.Entries
                .Select(e => new WaitTimeEntryResponse(
                    e.Source, e.IdleSeconds, e.BusySeconds, e.WaitingForHumanSeconds, e.StateTransitions))
                .ToList(),
            rollup.From,
            rollup.To);
}