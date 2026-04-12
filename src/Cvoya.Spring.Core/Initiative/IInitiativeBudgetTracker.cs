// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

/// <summary>
/// Tracks per-agent budget consumption for Tier 2 cognition invocations and enforces
/// caps defined in <see cref="Tier2Config"/>.
/// </summary>
public interface IInitiativeBudgetTracker
{
    /// <summary>
    /// Attempt to consume budget for an agent's Tier 2 invocation. Returns <c>false</c>
    /// if the estimated cost would exceed the agent's configured budget caps.
    /// </summary>
    /// <param name="agentId">The agent whose budget to consume.</param>
    /// <param name="estimatedCost">The estimated cost in dollars of the impending invocation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><c>true</c> if the budget permitted the consumption; otherwise <c>false</c>.</returns>
    Task<bool> TryConsumeAsync(string agentId, decimal estimatedCost, CancellationToken cancellationToken);
}