// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Persistence abstraction for the parent → child unit edge introduced
/// in #1154. Mirrors the <c>unit://</c>-scheme entries kept in
/// <c>UnitActor</c> state so cross-cutting readers (the tenant-tree
/// endpoint, analytics, the cloud overlay) can resolve the unit
/// containment graph without a per-unit actor round-trip.
/// </summary>
/// <remarks>
/// <para>
/// Defined in <c>Cvoya.Spring.Core</c> so the private cloud repo can
/// swap the implementation (tenant-aware wrapper, audit-logged decorator,
/// permission-checked overlay) via DI without taking a dependency on
/// <c>Cvoya.Spring.Dapr</c>. The default EF Core implementation lives
/// in <c>Cvoya.Spring.Dapr.Data</c> alongside
/// <see cref="IUnitMembershipRepository"/>.
/// </para>
/// <para>
/// The actor-state list remains authoritative for runtime dispatch and
/// cycle detection. This repository is a write-through projection: every
/// mutation should also flow through the actor, and a startup
/// reconciliation hosted service backfills rows for actors that already
/// carry sub-unit edges in state.
/// </para>
/// </remarks>
public interface IUnitSubunitMembershipRepository
{
    /// <summary>
    /// Creates the row for <c>(parentUnitId, childUnitId)</c> if it does
    /// not already exist; refreshes <c>UpdatedAt</c> otherwise. Idempotent
    /// — safe to call repeatedly from the actor write-through path and
    /// from the startup reconciliation service.
    /// </summary>
    Task UpsertAsync(Guid parentId, Guid childId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the row for the given composite key. No-op when no row
    /// matches — the operation is idempotent so the actor's
    /// <c>RemoveMemberAsync</c> can call it without branching on
    /// "row exists".
    /// </summary>
    Task DeleteAsync(Guid parentId, Guid childId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk-removes every edge that mentions the given unit, on either
    /// the parent or the child side. Used by the unit-delete cascade in
    /// <c>DirectoryService</c> so a tear-down purges both "this unit's
    /// children" and "this unit's parents" rows in one statement.
    /// </summary>
    Task DeleteAllForUnitAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every direct child unit id of the given parent, ordered
    /// by <c>CreatedAt</c> for stable iteration. The parent may be a
    /// tenant id (top-level units) or a unit id.
    /// </summary>
    Task<IReadOnlyList<UnitSubunitMembership>> ListByParentAsync(Guid parentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every direct parent unit id of the given child, ordered
    /// by <c>CreatedAt</c>. In the current model a child unit has at
    /// most one parent (1:N containment), but the column has no unique
    /// constraint so a multi-parent extension (#217) does not require a
    /// schema change.
    /// </summary>
    Task<IReadOnlyList<UnitSubunitMembership>> ListByChildAsync(Guid childId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every edge visible in the current tenant scope. Used by
    /// the tenant-tree endpoint (<c>GET /api/v1/tenant/tree</c>) to nest
    /// child units under their parent in a single query.
    /// </summary>
    Task<IReadOnlyList<UnitSubunitMembership>> ListAllAsync(CancellationToken cancellationToken = default);
}