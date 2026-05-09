// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

/// <summary>
/// Stores and resolves <see cref="InitiativePolicy"/> values for agents and units,
/// and computes the effective <see cref="InitiativeLevel"/> for an agent by combining
/// its own policy with the enclosing unit's ceiling.
/// </summary>
public interface IAgentPolicyStore
{
    /// <summary>
    /// Get the policy for a target (agent or unit). The target kind is encoded into
    /// <paramref name="targetId"/> by the caller.
    /// </summary>
    /// <param name="targetId">The agent or unit identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The resolved <see cref="InitiativePolicy"/>.</returns>
    Task<InitiativePolicy> GetPolicyAsync(string targetId, CancellationToken cancellationToken);

    /// <summary>
    /// Set the policy for a target (agent or unit).
    /// </summary>
    /// <param name="targetId">The agent or unit identifier.</param>
    /// <param name="policy">The policy to persist.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetPolicyAsync(string targetId, InitiativePolicy policy, CancellationToken cancellationToken);

    /// <summary>
    /// Get the effective initiative level for an agent, taking into account both the
    /// agent's own configuration and the enclosing unit's policy ceiling.
    /// </summary>
    /// <param name="agentId">The agent to query.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The effective <see cref="InitiativeLevel"/>.</returns>
    Task<InitiativeLevel> GetEffectiveLevelAsync(string agentId, CancellationToken cancellationToken);

    /// <summary>
    /// Record the enclosing unit for an agent. The mapping is consulted by
    /// <see cref="GetEffectiveLevelAsync"/> so that an agent with no agent-scoped policy
    /// inherits its unit's <see cref="InitiativePolicy.MaxLevel"/>. Pass <c>null</c> for
    /// <paramref name="unitId"/> to detach the agent from any unit.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="unitId">The enclosing unit identifier, or <c>null</c> to clear the assignment.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetAgentUnitAsync(string agentId, string? unitId, CancellationToken cancellationToken);

    /// <summary>
    /// Get the enclosing unit assigned to an agent via <see cref="SetAgentUnitAsync"/>,
    /// or <c>null</c> if no assignment has been recorded.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The enclosing unit identifier, or <c>null</c> if none is recorded.</returns>
    Task<string?> GetAgentUnitAsync(string agentId, CancellationToken cancellationToken);
}
