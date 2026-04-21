// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Globalization;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps the tenant-scoped cost time-series API surface (V21-tenant-cost-timeseries,
/// #916). Exposes <c>GET /api/v1/tenant/cost/timeseries</c> — a
/// zero-filled, bucketed cost series over a rolling window. Portal
/// consumers: <c>/budgets</c> sparkline, the forthcoming analytics
/// stacked-area chart (#910), and the 7-day trailing tile on
/// tenant-budgets (#902).
/// </summary>
public static class TenantCostEndpoints
{
    /// <summary>Default window when <c>window=</c> is omitted.</summary>
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    /// <summary>Default bucket grain when <c>bucket=</c> is omitted.</summary>
    private static readonly TimeSpan DefaultBucket = TimeSpan.FromDays(1);

    /// <summary>
    /// Cache window for the time-series payload. Per-minute cost
    /// aggregation is sufficient for the operator surfaces that consume
    /// this (sparkline + charts); a tighter window would churn the
    /// portal's query cache every operator nav bounce.
    /// </summary>
    private const int CacheMaxAgeSeconds = 60;

    /// <summary>Hard cap on the window size. 90 days matches the analytics
    /// pages' `--window 90d` ceiling — beyond that the chart axis becomes
    /// unreadable and the payload starts to matter.</summary>
    private static readonly TimeSpan MaxWindow = TimeSpan.FromDays(90);

    /// <summary>
    /// Registers the tenant cost time-series endpoint. Call from
    /// <c>Program.cs</c> alongside <c>MapCostEndpoints</c>. Returns the
    /// route group so callers can apply <c>RequireAuthorization()</c>.
    /// </summary>
    public static RouteGroupBuilder MapTenantCostEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/cost")
            .WithTags("Tenant");

        group.MapGet("/timeseries", GetTenantCostTimeseriesAsync)
            .WithName("GetTenantCostTimeseries")
            .WithSummary("Get a zero-filled tenant cost time-series bucketed over a rolling window")
            .Produces<CostTimeseriesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return group;
    }

    private static async Task<IResult> GetTenantCostTimeseriesAsync(
        HttpContext httpContext,
        [FromServices] ICostQueryService costQueryService,
        [FromServices] ITenantContext tenantContext,
        [FromQuery] string? window,
        [FromQuery] string? bucket,
        CancellationToken cancellationToken)
    {
        if (!TryParseDuration(window, DefaultWindow, out var windowSpan, out var windowError))
        {
            return Results.Problem(detail: windowError, statusCode: StatusCodes.Status400BadRequest);
        }

        if (windowSpan > MaxWindow)
        {
            return Results.Problem(
                detail: $"window must be <= {FormatDuration(MaxWindow)}.",
                statusCode: StatusCodes.Status400BadRequest);
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

        // Anchor `to` on the current UTC instant and clamp it to the start
        // of the next bucket so the final emitted bucket is the in-progress
        // one — portal consumers display "spend so far today" at the right
        // edge of the chart.
        var to = DateTimeOffset.UtcNow;
        var from = to - windowSpan;

        var tenantId = tenantContext.CurrentTenantId;

        var series = await costQueryService.GetTenantCostTimeseriesAsync(
            tenantId, from, to, bucketSpan, bucketLabel, cancellationToken);

        var response = new CostTimeseriesResponse(
            From: series.From,
            To: series.To,
            Bucket: series.Bucket,
            Series: series.Series
                .Select(b => new CostTimeseriesBucketResponse(b.BucketStart, b.Cost))
                .ToList());

        httpContext.Response.Headers.CacheControl =
            $"private, max-age={CacheMaxAgeSeconds.ToString(CultureInfo.InvariantCulture)}";

        return Results.Ok(response);
    }

    /// <summary>
    /// Parses a compact duration string of the form <c>&lt;N&gt;&lt;suffix&gt;</c>
    /// where the suffix is <c>m</c> (minutes), <c>h</c> (hours), or
    /// <c>d</c> (days). Returns <paramref name="fallback"/> when the input
    /// is null or empty. This is intentionally narrower than
    /// <see cref="TimeSpan.Parse(string)"/> — operators type
    /// <c>30d</c>, not <c>30.00:00:00</c>.
    /// </summary>
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

    /// <summary>
    /// Parses the bucket parameter. Only the portal-facing vocabulary is
    /// accepted (<c>1h</c>, <c>1d</c>, <c>7d</c>) — the chart axes are
    /// labelled around these exact grains and custom buckets would force
    /// the portal to branch on arbitrary strings.
    /// </summary>
    private static bool TryParseBucket(
        string? input,
        out TimeSpan bucket,
        out string label,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            bucket = DefaultBucket;
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

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1 && span.TotalDays == Math.Floor(span.TotalDays))
        {
            return $"{(int)span.TotalDays}d";
        }
        if (span.TotalHours >= 1 && span.TotalHours == Math.Floor(span.TotalHours))
        {
            return $"{(int)span.TotalHours}h";
        }
        return span.ToString("c", CultureInfo.InvariantCulture);
    }
}