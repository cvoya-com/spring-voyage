// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Initiative;

/// <summary>
/// Process-local, in-memory <see cref="IAgentPolicyStore"/> implementation backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. Retained as a test-friendly default;
/// production hosts register <see cref="DaprStateAgentPolicyStore"/> so policies survive
/// process restarts and are shared across replicas.
/// </summary>
public class InMemoryAgentPolicyStore : IAgentPolicyStore
{
    private readonly ConcurrentDictionary<string, InitiativePolicy> _policies = new();
    private readonly ConcurrentDictionary<string, string> _agentUnitAssignments = new();

    /// <inheritdoc />
    /// <remarks>
    /// Returns the policy previously supplied via <see cref="SetPolicyAsync"/> for the
    /// given target identifier, or a default <see cref="InitiativePolicy"/> (which is
    /// <see cref="InitiativeLevel.Passive"/>) when no policy has been stored.
    /// </remarks>
    public Task<InitiativePolicy> GetPolicyAsync(string targetId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetId);
        cancellationToken.ThrowIfCancellationRequested();

        var policy = _policies.TryGetValue(targetId, out var stored)
            ? stored
            : new InitiativePolicy();

        return Task.FromResult(policy);
    }

    /// <inheritdoc />
    public Task SetPolicyAsync(string targetId, InitiativePolicy policy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetId);
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();

        _policies[targetId] = policy;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resolution order:
    /// <list type="number">
    ///   <item>If an agent-scoped policy exists under <c>"agent:{agentId}"</c>, return its <see cref="InitiativePolicy.MaxLevel"/>.</item>
    ///   <item>Otherwise, if the agent has been assigned to a unit via <see cref="SetAgentUnitAsync"/> and that unit has a policy, return the unit's <see cref="InitiativePolicy.MaxLevel"/>.</item>
    ///   <item>Otherwise, return <see cref="InitiativeLevel.Passive"/>.</item>
    /// </list>
    /// </remarks>
    public Task<InitiativeLevel> GetEffectiveLevelAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        cancellationToken.ThrowIfCancellationRequested();

        var rawAgentId = StripAgentPrefix(agentId);
        var agentKey = $"agent:{rawAgentId}";

        if (_policies.TryGetValue(agentKey, out var agentPolicy))
        {
            return Task.FromResult(agentPolicy.MaxLevel);
        }

        if (_agentUnitAssignments.TryGetValue(rawAgentId, out var unitId)
            && _policies.TryGetValue($"unit:{unitId}", out var unitPolicy))
        {
            return Task.FromResult(unitPolicy.MaxLevel);
        }

        return Task.FromResult(InitiativeLevel.Passive);
    }

    /// <inheritdoc />
    public Task SetAgentUnitAsync(string agentId, string? unitId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        cancellationToken.ThrowIfCancellationRequested();

        var rawAgentId = StripAgentPrefix(agentId);
        if (string.IsNullOrEmpty(unitId))
        {
            _agentUnitAssignments.TryRemove(rawAgentId, out _);
        }
        else
        {
            _agentUnitAssignments[rawAgentId] = StripUnitPrefix(unitId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetAgentUnitAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        cancellationToken.ThrowIfCancellationRequested();

        var rawAgentId = StripAgentPrefix(agentId);
        return Task.FromResult(_agentUnitAssignments.TryGetValue(rawAgentId, out var unitId) ? unitId : null);
    }

    private static string StripAgentPrefix(string agentId)
        => agentId.StartsWith("agent:", StringComparison.Ordinal) ? agentId[6..] : agentId;

    private static string StripUnitPrefix(string unitId)
        => unitId.StartsWith("unit:", StringComparison.Ordinal) ? unitId[5..] : unitId;
}
