// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;

/// <summary>
/// Singleton seam over the scoped <c>IUnitLiveConfigRepository</c>.
/// <c>UnitActor</c> is not request-scoped, so it cannot consume the
/// scoped EF repository directly. The store creates a fresh DI scope
/// per call, resolves the repository, and forwards the operation.
/// Mirrors the scope-creating pattern already in use for
/// <c>IUnitHumanPermissionStore</c>,
/// <c>IUnitSubunitMembershipProjector</c>, and
/// <c>IAgentLiveConfigStore</c>.
/// </summary>
/// <remarks>
/// Registered as <c>TryAddSingleton</c> so cloud overlays can layer
/// audit logging or cross-tenant guards on top without displacing the
/// OSS default (per <c>AGENTS.md</c> § "Open-source platform and
/// extensibility").
/// </remarks>
public interface IUnitLiveConfigStore
{
    /// <summary>
    /// Returns the persisted metadata for <paramref name="unitId"/> in
    /// the current tenant. <c>DisplayName</c> / <c>Description</c> are
    /// always <c>null</c> — those values live on the directory entity
    /// (<c>unit_definitions</c>), not on the live-config row.
    /// </summary>
    Task<UnitMetadata> GetMetadataAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the partial-PATCH semantics of
    /// <see cref="UnitMetadata"/>: only Model / Color / Provider /
    /// Hosting non-<c>null</c> fields are written. Returns the field
    /// names that were actually written so the actor can build a
    /// meaningful <c>StateChanged</c> activity event.
    /// </summary>
    Task<IReadOnlyList<string>> UpsertMetadataAsync(
        Guid unitId, UnitMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the unit's persisted <see cref="UnitBoundary"/>, or
    /// <see cref="UnitBoundary.Empty"/> when no row / column value
    /// exists.
    /// </summary>
    Task<UnitBoundary> GetBoundaryAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the unit's boundary in full.</summary>
    Task SetBoundaryAsync(Guid unitId, UnitBoundary boundary, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the unit's persisted permission-inheritance flag,
    /// defaulting to <see cref="UnitPermissionInheritance.Inherit"/>
    /// when no row exists.
    /// </summary>
    Task<UnitPermissionInheritance> GetPermissionInheritanceAsync(
        Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>Sets the unit's permission-inheritance flag.</summary>
    Task SetPermissionInheritanceAsync(
        Guid unitId,
        UnitPermissionInheritance inheritance,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the unit's own-expertise list, sorted by domain name.
    /// </summary>
    Task<ExpertiseDomain[]> GetOwnExpertiseAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the unit's own-expertise list in full. Returns the
    /// normalised list that was actually persisted.
    /// </summary>
    Task<ExpertiseDomain[]> SetOwnExpertiseAsync(
        Guid unitId, IReadOnlyList<ExpertiseDomain> domains, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when an own-expertise list has been written
    /// for <paramref name="unitId"/> at all (even an empty list). Used
    /// by the activation seeder to honour the "actor state wins"
    /// precedence rule (ADR-0040 / #488).
    /// </summary>
    Task<bool> HasOwnExpertiseSetAsync(Guid unitId, CancellationToken cancellationToken = default);
}
