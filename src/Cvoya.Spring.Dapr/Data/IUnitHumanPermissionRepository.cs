// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Dapr.Auth;

/// <summary>
/// Persistence abstraction for the <c>unit_human_permissions</c> table
/// introduced in #2044 / ADR-0040. Encapsulates the (tenant, unit, human)
/// uniqueness invariant so both the actor write path and the
/// <c>PermissionService</c> read path agree on the schema without either
/// taking a direct dependency on <see cref="SpringDbContext"/>.
/// </summary>
/// <remarks>
/// The interface lives in <c>Cvoya.Spring.Dapr.Data</c> rather than
/// <c>Cvoya.Spring.Core</c> because <see cref="PermissionLevel"/> and
/// <see cref="UnitPermissionEntry"/> already live in <c>Cvoya.Spring.Dapr</c>.
/// Cloud overlays may swap the implementation through DI (TryAdd) without
/// touching either consumer.
/// </remarks>
public interface IUnitHumanPermissionRepository
{
    /// <summary>
    /// Creates or updates the grant for <c>(unitId, humanId)</c> in the
    /// current tenant. <see cref="UnitPermissionEntry.HumanId"/> on
    /// <paramref name="entry"/> is ignored — the typed
    /// <paramref name="humanId"/> is authoritative.
    /// </summary>
    Task UpsertAsync(Guid unitId, Guid humanId, UnitPermissionEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the grant for <c>(unitId, humanId)</c> in the current tenant.
    /// Returns <c>true</c> when a row was deleted; <c>false</c> when no row
    /// existed (idempotent — the API endpoint always returns 204 regardless).
    /// </summary>
    Task<bool> DeleteAsync(Guid unitId, Guid humanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the direct grant for <c>(unitId, humanId)</c> in the current
    /// tenant, or <c>null</c> when no grant exists.
    /// </summary>
    Task<UnitPermissionEntry?> GetAsync(Guid unitId, Guid humanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every grant recorded against <paramref name="unitId"/> in the
    /// current tenant. Used by <c>GET /api/v1/units/{id}/humans</c>.
    /// </summary>
    Task<IReadOnlyList<UnitPermissionEntry>> ListByUnitAsync(Guid unitId, CancellationToken cancellationToken = default);
}
