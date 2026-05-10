// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Singleton seam over the EF-backed unit member graph (<c>unit_memberships</c>
/// for agent-typed members, <c>unit_subunit_memberships</c> for unit-typed
/// members and the explicit tenant-root edge). Replaces the actor-state
/// <c>Unit:Members</c> dual-storage that ADR-0040 removed: <c>UnitActor</c>
/// reads / writes the member graph through this seam on every call, with no
/// actor-state mirror.
/// </summary>
/// <remarks>
/// <para>
/// Defined in <c>Cvoya.Spring.Core</c> so the cloud overlay can register a
/// tenant-aware decorator (audit logging, permission checks) ahead of the
/// OSS default without taking a dependency on <c>Cvoya.Spring.Dapr</c>. Per
/// the <c>TryAdd*</c> rule, production DI registers the default with
/// <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Type)" />
/// so the cloud registration takes precedence when present.
/// </para>
/// <para>
/// The store is the only write path for the member graph (#2052 / ADR-0040 §
/// "unit_memberships authority"). Endpoint-level repositories
/// (<see cref="IUnitMembershipRepository" />,
/// <see cref="IUnitSubunitMembershipRepository" />) target the same EF
/// tables for the typed-overrides / cascade-aware semantics the API layer
/// needs; the actor's add / remove mutations route through this store with
/// idempotent semantics so the actor and the endpoints converge on the same
/// row.
/// </para>
/// </remarks>
public interface IUnitMemberGraphStore
{
    /// <summary>
    /// Returns the unit's current member set as a flat list of
    /// <see cref="Address" /> values: agent members from
    /// <c>unit_memberships</c> and direct sub-unit children from
    /// <c>unit_subunit_memberships</c> (parent-side). Tenant-root edges
    /// (<c>parent_id == tenant.id</c>) are not members of the unit and
    /// never appear here. Order is stable: agents first by <c>CreatedAt</c>,
    /// then sub-units by <c>CreatedAt</c>.
    /// </summary>
    Task<IReadOnlyList<Address>> GetMembersAsync(
        Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently adds the agent-scheme edge
    /// <c>(unitId, agentId)</c> to <c>unit_memberships</c> with default
    /// per-membership values. Existing rows are preserved (the typed
    /// override fields stay intact). Returns <c>true</c> when a new row
    /// was inserted; <c>false</c> when the row already existed.
    /// </summary>
    Task<bool> AddAgentMemberAsync(
        Guid unitId, Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently adds the unit-scheme edge
    /// <c>(parentId, childId)</c> to <c>unit_subunit_memberships</c>.
    /// Returns <c>true</c> when a new row was inserted; <c>false</c> when
    /// the edge already existed. <paramref name="parentId" /> is a unit
    /// id; for the tenant-root edge call <see cref="EnsureTopLevelEdgeAsync" />.
    /// </summary>
    Task<bool> AddSubunitMemberAsync(
        Guid parentId, Guid childId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently removes the agent-scheme edge
    /// <c>(unitId, agentId)</c> from <c>unit_memberships</c>. Returns
    /// <c>true</c> when a row was deleted; <c>false</c> when no row
    /// existed. Bypasses the "agent must keep at least one membership"
    /// guard enforced by <see cref="IUnitMembershipRepository.DeleteAsync" />
    /// — the actor surface is the idempotent edge writer, not the
    /// invariant enforcer; endpoints that need the guard call the
    /// repository directly.
    /// </summary>
    Task<bool> RemoveAgentMemberAsync(
        Guid unitId, Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently removes the unit-scheme edge
    /// <c>(parentId, childId)</c> from <c>unit_subunit_memberships</c>.
    /// Returns <c>true</c> when a row was deleted; <c>false</c> when no
    /// edge existed.
    /// </summary>
    Task<bool> RemoveSubunitMemberAsync(
        Guid parentId, Guid childId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the direct sub-unit children of <paramref name="parentId" />
    /// (excluding tenant-root edges). Used by
    /// <see cref="IUnitMembershipCoordinator" /> during cycle detection so
    /// the walk runs against EF instead of recursing through the actor
    /// proxy graph.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListDirectSubunitChildrenAsync(
        Guid parentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures a tenant-root edge exists for <paramref name="unitId" /> in
    /// <c>unit_subunit_memberships</c> with <c>parent_id == tenantId</c>.
    /// This is the v0.1 explicit top-level model: a unit is top-level iff
    /// it has exactly one parent edge whose parent id equals its tenant id
    /// (#2052 / ADR-0040). Idempotent — safe to call from both the
    /// creation path and any future repair / re-parent flow.
    /// </summary>
    Task EnsureTopLevelEdgeAsync(
        Guid unitId, Guid tenantId, CancellationToken cancellationToken = default);
}
