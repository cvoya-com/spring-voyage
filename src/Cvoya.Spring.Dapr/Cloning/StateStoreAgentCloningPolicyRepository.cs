// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Cloning;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;

/// <summary>
/// <see cref="IAgentCloningPolicyRepository"/> implementation backed by
/// <see cref="IStateStore"/> (#416). Chosen over a new EF table so the
/// record follows the same durability story as every other agent-scoped
/// runtime setting (skills, model hint, budget) — the Dapr state store
/// survives restarts, is shared across replicas, and is what a cloud
/// host is already expected to back with a tenant-scoped component.
/// </summary>
/// <remarks>
/// <para>
/// Keys:
/// <list type="bullet">
///   <item><c>Agent:CloningPolicy:agent:{id}</c> for
///     <see cref="CloningPolicyScope.Agent"/>.</item>
///   <item><c>Tenant:CloningPolicy:{tenantId}</c> for
///     <see cref="CloningPolicyScope.Tenant"/>.</item>
/// </list>
/// Both live under the same state-store component the rest of the Dapr
/// stack uses, so no new component wiring is needed.
/// </para>
/// <para>
/// An all-null policy (<see cref="AgentCloningPolicy.IsEmpty"/>) is
/// deleted rather than stored as an inert row so the store reflects
/// scopes that actually have a policy.
/// </para>
/// </remarks>
public class StateStoreAgentCloningPolicyRepository(IStateStore stateStore) : IAgentCloningPolicyRepository
{
    /// <inheritdoc />
    public async Task<AgentCloningPolicy> GetAsync(
        CloningPolicyScope scope,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        var stored = await stateStore.GetAsync<AgentCloningPolicy>(KeyFor(scope, targetId), cancellationToken);
        return stored ?? AgentCloningPolicy.Empty;
    }

    /// <inheritdoc />
    public async Task SetAsync(
        CloningPolicyScope scope,
        string targetId,
        AgentCloningPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(policy);

        // An all-null policy is "no constraint at this scope" — drop the
        // row so /GET returns Empty from the absent-row branch instead of
        // a persisted Empty payload.
        if (policy.IsEmpty)
        {
            await DeleteAsync(scope, targetId, cancellationToken);
            return;
        }

        await stateStore.SetAsync(KeyFor(scope, targetId), policy, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(
        CloningPolicyScope scope,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        return stateStore.DeleteAsync(KeyFor(scope, targetId), cancellationToken);
    }

    private static string KeyFor(CloningPolicyScope scope, string targetId) => scope switch
    {
        CloningPolicyScope.Agent => $"{StateKeys.AgentCloningPolicy}:{targetId}",
        CloningPolicyScope.Tenant => $"{StateKeys.TenantCloningPolicy}:{targetId}",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown cloning-policy scope."),
    };
}