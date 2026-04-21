// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Costs;

/// <summary>
/// Provides aggregated cost queries per agent, unit, and tenant.
/// </summary>
public interface ICostQueryService
{
    /// <summary>
    /// Gets the aggregated cost summary for a specific agent within a time range.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="from">The start of the time range.</param>
    /// <param name="to">The end of the time range.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cost summary for the agent.</returns>
    Task<CostSummary> GetAgentCostAsync(string agentId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the aggregated cost summary for a specific unit within a time range.
    /// </summary>
    /// <param name="unitId">The unit identifier.</param>
    /// <param name="from">The start of the time range.</param>
    /// <param name="to">The end of the time range.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cost summary for the unit.</returns>
    Task<CostSummary> GetUnitCostAsync(string unitId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the aggregated cost summary for a specific tenant within a time range.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="from">The start of the time range.</param>
    /// <param name="to">The end of the time range.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cost summary for the tenant.</returns>
    Task<CostSummary> GetTenantCostAsync(string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a tenant-wide cost time-series bucketed by fixed-size UTC
    /// intervals. Missing buckets are zero-filled so the returned series
    /// is continuous from <paramref name="from"/> to <paramref name="to"/>
    /// — consumers (the portal sparkline, analytics charts) can render a
    /// connected line without inventing data.
    /// </summary>
    /// <remarks>
    /// Buckets are anchored on <paramref name="from"/> and advance by
    /// <paramref name="bucket"/> in UTC wall-clock time — DST transitions
    /// do not shift bucket edges. The sum across every bucket equals
    /// <see cref="GetTenantCostAsync"/> called with the same window.
    /// </remarks>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="from">The inclusive UTC start of the window.</param>
    /// <param name="to">The exclusive UTC end of the window.</param>
    /// <param name="bucket">The bucket size; must be strictly positive and no larger than the window.</param>
    /// <param name="bucketLabel">Canonical bucket label (e.g. <c>"1h"</c>, <c>"1d"</c>) persisted on the response.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The zero-filled time-series for the tenant.</returns>
    Task<CostTimeseries> GetTenantCostTimeseriesAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucket,
        string bucketLabel,
        CancellationToken cancellationToken = default);
}