// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Singleton seam over the scoped <c>IUnitHumanPermissionRepository</c>.
/// Mirrors the scope-creating pattern used by
/// <c>UnitMemberGraphStore</c>: actors are not request-scoped, so
/// they cannot consume a scoped EF repository directly. The store creates a
/// fresh DI scope per call, resolves the repository, and translates the
/// (unit, human) write into a tenant-scoped row mutation.
/// </summary>
/// <remarks>
/// Registered as <c>TryAddSingleton</c> so cloud overlays can layer audit
/// logging or cross-tenant guards on top without displacing the OSS default
/// (per <c>AGENTS.md</c> § "Source-available platform and extensibility").
/// </remarks>
public interface IUnitHumanPermissionStore
{
    /// <summary>
    /// Creates or updates the (unit, human) grant. Replaces the actor-state
    /// <c>Unit:HumanPermissions</c> map write that this seam supersedes.
    /// </summary>
    Task UpsertAsync(Guid unitId, Guid humanId, UnitPermissionEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the (unit, human) grant. Returns <c>true</c> when a row was
    /// deleted; <c>false</c> when no row existed (idempotent).
    /// </summary>
    Task<bool> DeleteAsync(Guid unitId, Guid humanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the direct <see cref="PermissionLevel"/> for (unit, human),
    /// or <c>null</c> when no grant exists.
    /// </summary>
    Task<PermissionLevel?> GetPermissionAsync(Guid unitId, Guid humanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every grant recorded against <paramref name="unitId"/> in the
    /// current tenant.
    /// </summary>
    Task<UnitPermissionEntry[]> ListByUnitAsync(Guid unitId, CancellationToken cancellationToken = default);
}
