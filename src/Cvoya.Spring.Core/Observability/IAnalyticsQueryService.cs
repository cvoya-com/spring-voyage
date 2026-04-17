// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Provides analytics rollups that power the portal's Analytics surface and
/// the `spring analytics` CLI commands (#457). Costs keep their own service
/// (<see cref="Cvoya.Spring.Core.Costs.ICostQueryService"/>) so this interface
/// only adds the new slices — throughput and wait times.
/// </summary>
public interface IAnalyticsQueryService
{
    /// <summary>
    /// Aggregates message / turn / tool-call counts per source over a time range.
    /// </summary>
    /// <param name="sourceFilter">
    /// Optional substring filter on the source address (e.g. <c>agent://</c>
    /// or <c>unit://eng-team</c>). When null, all sources are included.
    /// </param>
    /// <param name="from">Start of the rollup window.</param>
    /// <param name="to">End of the rollup window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregated throughput rollup.</returns>
    Task<ThroughputRollup> GetThroughputAsync(
        string? sourceFilter,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregates idle / busy / waiting-on-human durations per source over a
    /// time range. Duration fields are placeholders until PR-PLAT-OBS-1 (#391)
    /// supplies the underlying start/end timestamps; the <c>StateTransitions</c>
    /// counter is the fallback signal.
    /// </summary>
    /// <param name="sourceFilter">Optional substring filter on the source address.</param>
    /// <param name="from">Start of the rollup window.</param>
    /// <param name="to">End of the rollup window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregated wait-time rollup.</returns>
    Task<WaitTimeRollup> GetWaitTimesAsync(
        string? sourceFilter,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);
}