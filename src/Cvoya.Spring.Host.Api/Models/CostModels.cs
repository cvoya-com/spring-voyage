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