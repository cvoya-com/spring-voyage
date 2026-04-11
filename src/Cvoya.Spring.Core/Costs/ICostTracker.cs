// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Costs;

/// <summary>
/// Tracks and attributes costs for clone operations to the parent agent.
/// </summary>
public interface ICostTracker
{
    /// <summary>
    /// Attributes a cost to a parent agent for a specific clone operation.
    /// </summary>
    /// <param name="parentAgentId">The parent agent identifier.</param>
    /// <param name="cloneId">The clone identifier.</param>
    /// <param name="cost">The cost to attribute.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task AttributeCostAsync(string parentAgentId, string cloneId, decimal cost, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the aggregated cost for an agent, including all clone costs attributed to it.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The total aggregated cost for the agent.</returns>
    Task<decimal> GetAggregatedCostAsync(string agentId, CancellationToken cancellationToken);
}