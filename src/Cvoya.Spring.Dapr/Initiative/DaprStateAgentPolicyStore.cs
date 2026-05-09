// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.State;

/// <summary>
/// Dapr state-store-backed <see cref="IAgentPolicyStore"/> implementation.
/// Policies are keyed by <c>"Initiative:Policy:agent:{id}"</c> or
/// <c>"Initiative:Policy:unit:{id}"</c> so they survive process restarts and are shared
/// across replicas. An auxiliary <c>"Initiative:AgentUnit:{agentId}"</c> entry records
/// the agent-to-unit assignment used by <see cref="GetEffectiveLevelAsync"/> to apply
/// the unit-ceiling rule when no agent-scoped policy is set.
/// </summary>
public class DaprStateAgentPolicyStore(IStateStore stateStore) : IAgentPolicyStore
{
    private const string PolicyPrefix = "Initiative:Policy:";
    private const string AgentUnitPrefix = "Initiative:AgentUnit:";

    /// <inheritdoc />
    public async Task<InitiativePolicy> GetPolicyAsync(string targetId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetId);

        var stored = await stateStore.GetAsync<InitiativePolicy>(PolicyKey(targetId), cancellationToken);
        return stored ?? new InitiativePolicy();
    }

    /// <inheritdoc />
    public Task SetPolicyAsync(string targetId, InitiativePolicy policy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetId);
        ArgumentNullException.ThrowIfNull(policy);

        return stateStore.SetAsync(PolicyKey(targetId), policy, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resolution order:
    /// <list type="number">
    ///   <item>If an agent-scoped policy exists, return its <see cref="InitiativePolicy.MaxLevel"/>.</item>
    ///   <item>Otherwise, if the agent has a unit assignment and that unit has a policy, return the unit's <see cref="InitiativePolicy.MaxLevel"/>.</item>
    ///   <item>Otherwise, return <see cref="InitiativeLevel.Passive"/>.</item>
    /// </list>
    /// </remarks>
    public async Task<InitiativeLevel> GetEffectiveLevelAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        var rawAgentId = StripAgentPrefix(agentId);
        var agentPolicy = await stateStore.GetAsync<InitiativePolicy>(PolicyKey($"agent:{rawAgentId}"), cancellationToken);
        if (agentPolicy is not null)
        {
            return agentPolicy.MaxLevel;
        }

        var unitId = await stateStore.GetAsync<string>(AgentUnitKey(rawAgentId), cancellationToken);
        if (!string.IsNullOrEmpty(unitId))
        {
            var unitPolicy = await stateStore.GetAsync<InitiativePolicy>(PolicyKey($"unit:{unitId}"), cancellationToken);
            if (unitPolicy is not null)
            {
                return unitPolicy.MaxLevel;
            }
        }

        return InitiativeLevel.Passive;
    }

    /// <inheritdoc />
    public Task SetAgentUnitAsync(string agentId, string? unitId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        var rawAgentId = StripAgentPrefix(agentId);
        if (string.IsNullOrEmpty(unitId))
        {
            return stateStore.DeleteAsync(AgentUnitKey(rawAgentId), cancellationToken);
        }

        return stateStore.SetAsync(AgentUnitKey(rawAgentId), StripUnitPrefix(unitId), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> GetAgentUnitAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        var rawAgentId = StripAgentPrefix(agentId);
        var unitId = await stateStore.GetAsync<string>(AgentUnitKey(rawAgentId), cancellationToken);
        return string.IsNullOrEmpty(unitId) ? null : unitId;
    }

    private static string PolicyKey(string targetId) => $"{PolicyPrefix}{targetId}";

    private static string AgentUnitKey(string rawAgentId) => $"{AgentUnitPrefix}{rawAgentId}";

    private static string StripAgentPrefix(string agentId)
        => agentId.StartsWith("agent:", StringComparison.Ordinal) ? agentId[6..] : agentId;

    private static string StripUnitPrefix(string unitId)
        => unitId.StartsWith("unit:", StringComparison.Ordinal) ? unitId[5..] : unitId;
}
