// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Provides query operations for activity events, including filtered pagination,
/// recent event retrieval, and cost aggregation.
/// </summary>
public interface IActivityQueryService
{
    /// <summary>
    /// Queries activity events with optional filters and pagination.
    /// </summary>
    /// <param name="parameters">The query parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A paginated result of activity events.</returns>
    Task<ActivityQueryResult> QueryAsync(ActivityQueryParameters parameters, CancellationToken cancellationToken);

    /// <summary>
    /// Gets activity events that occurred after the specified timestamp.
    /// </summary>
    /// <param name="since">The timestamp to query from.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of recent activity event items.</returns>
    Task<IReadOnlyList<ActivityQueryResult.Item>> GetRecentAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the total cost, optionally filtered by source and time range.
    /// </summary>
    /// <param name="source">Optional source filter.</param>
    /// <param name="from">Optional start of time range.</param>
    /// <param name="to">Optional end of time range.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The total cost.</returns>
    Task<decimal> GetTotalCostAsync(string? source, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken);

    /// <summary>
    /// Gets cost aggregated by source within an optional time range.
    /// </summary>
    /// <param name="from">Optional start of time range.</param>
    /// <param name="to">Optional end of time range.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of cost-by-source aggregations.</returns>
    Task<IReadOnlyList<CostBySource>> GetCostBySourceAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken);
}