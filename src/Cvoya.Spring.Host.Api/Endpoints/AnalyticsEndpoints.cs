// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Globalization;

using Cvoya.Spring.Core.Costs;
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
        var group = app.MapGroup("/api/v1/tenant/analytics")
            .WithTags("Analytics");

        group.MapGet("/throughput", GetThroughputAsync)
            .WithName("GetAnalyticsThroughput")
            .WithSummary("Get throughput counters (messages / turns / tool calls) per source over a time range")
            .Produces<ThroughputRollupResponse>(StatusCodes.Status200OK);

        group.MapGet("/waits", GetWaitTimesAsync)
            .WithName("GetAnalyticsWaits")
            .WithSummary("Get wait-time rollups per source; durations are computed from paired StateChanged lifecycle transitions")
            .Produces<WaitTimeRollupResponse>(StatusCodes.Status200OK);

        // #569 — per-agent and per-unit cost sparkline series.
        group.MapGet("/agents/{id}/cost-timeseries", GetAgentCostTimeseriesAsync)
            .WithName("GetAgentCostTimeseries")
            .WithSummary("Get a zero-filled cost time-series for an agent, suitable for sparkline rendering")
            .Produces<AnalyticsCostTimeseriesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/units/{id}/cost-timeseries", GetUnitCostTimeseriesAsync)
            .WithName("GetUnitCostTimeseries")
            .WithSummary("Get a zero-filled cost time-series for a unit, suitable for sparkline rendering")
            .Produces<AnalyticsCostTimeseriesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

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
                    e.Source, e.MessagesArrived, e.MessagesSent, e.Turns, e.ToolCalls))
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

    // #569 — cost time-series for individual agents and units.

    private static async Task<IResult> GetAgentCostTimeseriesAsync(
        string id,
        ICostQueryService costQueryService,
        string? window,
        string? bucket,
        CancellationToken cancellationToken)
    {
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(id, out var idGuid))
        {
            return Results.Problem(detail: $"Agent id '{id}' is not a valid Guid.", statusCode: StatusCodes.Status400BadRequest);
        }
        return await GetCostTimeseriesAsync(
            id, "agents", costQueryService,
            (svc, from, to, b, lbl, ct) => svc.GetAgentCostTimeseriesAsync(idGuid, from, to, b, lbl, ct),
            window, bucket, cancellationToken);
    }

    private static async Task<IResult> GetUnitCostTimeseriesAsync(
        string id,
        ICostQueryService costQueryService,
        string? window,
        string? bucket,
        CancellationToken cancellationToken)
    {
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(id, out var idGuid))
        {
            return Results.Problem(detail: $"Unit id '{id}' is not a valid Guid.", statusCode: StatusCodes.Status400BadRequest);
        }
        return await GetCostTimeseriesAsync(
            id, "units", costQueryService,
            (svc, from, to, b, lbl, ct) => svc.GetUnitCostTimeseriesAsync(idGuid, from, to, b, lbl, ct),
            window, bucket, cancellationToken);
    }

    private static async Task<IResult> GetCostTimeseriesAsync(
        string id,
        string scope,
        ICostQueryService costQueryService,
        Func<ICostQueryService, DateTimeOffset, DateTimeOffset, TimeSpan, string, CancellationToken, Task<Core.Costs.CostTimeseries>> fetch,
        string? window,
        string? bucket,
        CancellationToken cancellationToken)
    {
        if (!TryParseDuration(window, TimeSpan.FromDays(30), out var windowSpan, out var windowError))
        {
            return Results.Problem(detail: windowError, statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParseBucket(bucket, out var bucketSpan, out var bucketLabel, out var bucketError))
        {
            return Results.Problem(detail: bucketError, statusCode: StatusCodes.Status400BadRequest);
        }

        if (bucketSpan > windowSpan)
        {
            return Results.Problem(
                detail: "bucket must not exceed window.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var to = DateTimeOffset.UtcNow;
        var from = to - windowSpan;

        var series = await fetch(costQueryService, from, to, bucketSpan, bucketLabel, cancellationToken);

        var response = new AnalyticsCostTimeseriesResponse(
            Scope: scope,
            Id: id,
            Bucket: series.Bucket,
            From: series.From,
            To: series.To,
            Points: series.Series
                .Select(b => new AnalyticsCostTimeseriesBucketResponse(b.BucketStart, b.Cost))
                .ToList());

        return Results.Ok(response);
    }

    // Bucket / duration parsing — mirrors TenantCostEndpoints to keep the
    // three analytics verbs and the cost timeseries on the same vocabulary.

    private static bool TryParseDuration(
        string? input,
        TimeSpan fallback,
        out TimeSpan value,
        out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            value = fallback;
            return true;
        }

        var trimmed = input.Trim();
        if (trimmed.Length < 2)
        {
            value = default;
            error = $"Invalid duration '{input}'. Use e.g. '30d', '24h', '15m'.";
            return false;
        }

        var suffix = trimmed[^1];
        var numeric = trimmed[..^1];
        if (!int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            value = default;
            error = $"Invalid duration '{input}'. Use e.g. '30d', '24h', '15m'.";
            return false;
        }

        value = suffix switch
        {
            'm' or 'M' => TimeSpan.FromMinutes(n),
            'h' or 'H' => TimeSpan.FromHours(n),
            'd' or 'D' => TimeSpan.FromDays(n),
            _ => TimeSpan.Zero,
        };

        if (value == TimeSpan.Zero)
        {
            error = $"Invalid duration suffix in '{input}'. Valid suffixes: m, h, d.";
            return false;
        }

        return true;
    }

    private static bool TryParseBucket(
        string? input,
        out TimeSpan bucket,
        out string label,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            bucket = TimeSpan.FromDays(1);
            label = "1d";
            error = string.Empty;
            return true;
        }

        switch (input.Trim().ToLowerInvariant())
        {
            case "1h":
                bucket = TimeSpan.FromHours(1);
                label = "1h";
                error = string.Empty;
                return true;
            case "1d":
                bucket = TimeSpan.FromDays(1);
                label = "1d";
                error = string.Empty;
                return true;
            case "7d":
                bucket = TimeSpan.FromDays(7);
                label = "7d";
                error = string.Empty;
                return true;
            default:
                bucket = default;
                label = string.Empty;
                error = $"Invalid bucket '{input}'. Valid buckets: 1h, 1d, 7d.";
                return false;
        }
    }
}
