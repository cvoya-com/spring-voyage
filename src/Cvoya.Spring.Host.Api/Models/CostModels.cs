// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Response body representing an aggregated cost summary.
/// </summary>
/// <param name="TotalCost">The total cost in USD.</param>
/// <param name="TotalInputTokens">The total number of input tokens consumed.</param>
/// <param name="TotalOutputTokens">The total number of output tokens generated.</param>
/// <param name="RecordCount">The number of individual cost records in the aggregation.</param>
/// <param name="WorkCost">The portion of <paramref name="TotalCost"/> attributable to normal agent work.</param>
/// <param name="InitiativeCost">The portion of <paramref name="TotalCost"/> attributable to the initiative (reflection) loop.</param>
/// <param name="From">The start of the aggregation time range.</param>
/// <param name="To">The end of the aggregation time range.</param>
public record CostSummaryResponse(
    decimal TotalCost,
    long TotalInputTokens,
    long TotalOutputTokens,
    int RecordCount,
    decimal WorkCost,
    decimal InitiativeCost,
    DateTimeOffset From,
    DateTimeOffset To);

/// <summary>
/// One bucket of aggregated cost within a tenant-wide time-series
/// (<see cref="CostTimeseriesResponse"/>).
/// </summary>
/// <param name="T">Inclusive UTC start of the bucket.</param>
/// <param name="Cost">Total cost (USD) accumulated inside <c>[T, T + bucket)</c>. Always emitted — zero for empty buckets — so consumers can render a continuous line.</param>
public record CostTimeseriesBucketResponse(DateTimeOffset T, decimal Cost);

/// <summary>
/// Response body for <c>GET /api/v1/tenant/cost/timeseries</c>. A
/// zero-filled cost time-series bucketed by fixed UTC intervals anchored
/// on <paramref name="From"/>. The <paramref name="Bucket"/> field echoes
/// the request's canonical label (<c>"1h"</c> / <c>"1d"</c> / <c>"7d"</c>)
/// so the portal can label the x-axis without re-deriving it.
/// </summary>
/// <param name="From">Inclusive UTC start of the window.</param>
/// <param name="To">Exclusive UTC end of the window.</param>
/// <param name="Bucket">Canonical bucket label (<c>"1h"</c>, <c>"1d"</c>, <c>"7d"</c>).</param>
/// <param name="Series">Ordered bucket list; <c>Series[0].T == From</c>.</param>
public record CostTimeseriesResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    string Bucket,
    IReadOnlyList<CostTimeseriesBucketResponse> Series);

/// <summary>
/// One entry in a per-agent cost breakdown response. Corresponds to
/// <see cref="Cvoya.Spring.Core.Costs.CostBreakdownEntry"/>.
/// </summary>
/// <param name="Key">
/// The dimension value — a model name (e.g. <c>claude-3-5-sonnet</c>) when
/// <paramref name="Kind"/> is <c>model</c>, or a tool name when
/// <paramref name="Kind"/> is <c>tool</c>.
/// </param>
/// <param name="Kind">The cost kind: <c>model</c> for LLM token cost rows.</param>
/// <param name="TotalCost">Total cost (USD) for this key across the requested window.</param>
/// <param name="RecordCount">Number of individual cost records that contributed to <paramref name="TotalCost"/>.</param>
public record CostBreakdownEntryResponse(
    string Key,
    string Kind,
    decimal TotalCost,
    int RecordCount);

/// <summary>
/// Response body for <c>GET /api/v1/tenant/cost/agents/{id}/breakdown</c>.
/// Returns one entry per distinct model used by the agent over the window,
/// ordered by total cost descending (#570).
/// </summary>
/// <param name="AgentId">The agent identifier.</param>
/// <param name="From">Start of the aggregation window.</param>
/// <param name="To">End of the aggregation window.</param>
/// <param name="Entries">Per-model breakdown entries, descending by cost.</param>
public record CostBreakdownResponse(
    string AgentId,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<CostBreakdownEntryResponse> Entries);
