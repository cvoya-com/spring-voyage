// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Issues;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Computes the transitive issue rollup for a unit (#2160). Walks the
/// member graph (sub-units + member agents) recursively, batches the
/// resulting subjects through <see cref="IIssueReader.CountOpenAsync"/>,
/// and projects per-immediate-child summaries the Overview UI uses to
/// drill into the source of the rollup.
/// </summary>
public interface IIssueAggregator
{
    /// <summary>
    /// Returns the unit's own open issues plus the rolled-up
    /// descendant counts. Pure read; the rollup is not cached
    /// in v0.1 (queries are batched, so the cost is one membership
    /// walk + one grouped count query per call).
    /// </summary>
    Task<IssuesView> AggregateForUnitAsync(
        System.Guid unitId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the agent's own open issues. Agents have no
    /// descendants — the descendant rollup is always empty — but the
    /// shape stays uniform with units so the API can serialise the
    /// same envelope.
    /// </summary>
    Task<IssuesView> AggregateForAgentAsync(
        System.Guid agentId,
        CancellationToken cancellationToken = default);
}
