// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;

/// <summary>
/// Persistence abstraction for the unit live-config tables introduced
/// in #2049 / ADR-0040: <c>unit_live_config</c> (1:1) and
/// <c>unit_expertise</c> (N). Encapsulates the single-row-per-(tenant,
/// unit, key) invariants so the actor write path and any read consumer
/// agree on the schema without taking a direct dependency on
/// <see cref="SpringDbContext"/>.
/// </summary>
/// <remarks>
/// The interface lives in <c>Cvoya.Spring.Dapr.Data</c> rather than
/// <c>Cvoya.Spring.Core</c> because the implementation is EF-specific and
/// some shapes (<c>UnitBoundary</c>) cross the Dapr boundary already.
/// Cloud overlays may swap the implementation through DI (TryAdd) without
/// touching consumers.
/// </remarks>
public interface IUnitLiveConfigRepository
{
    /// <summary>
    /// Returns the persisted metadata for <paramref name="unitId"/> in
    /// the current tenant.
    /// <see cref="UnitMetadata.DisplayName"/> /
    /// <see cref="UnitMetadata.Description"/> are always returned as
    /// <c>null</c> — those values live on
    /// <c>unit_definitions</c> (the directory entity), not on the live
    /// config row.
    /// </summary>
    Task<UnitMetadata> GetMetadataAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the partial-PATCH semantics of
    /// <see cref="UnitMetadata"/>: only the non-<c>null</c> Model /
    /// Color / Provider / Hosting fields are written. DisplayName and
    /// Description are ignored — the directory entity owns them.
    /// Creates a row when none exists. Returns the field names that
    /// were actually written so the caller can build a meaningful
    /// <c>StateChanged</c> activity event.
    /// </summary>
    Task<IReadOnlyList<string>> UpsertMetadataAsync(
        Guid unitId, UnitMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the unit's persisted <see cref="UnitBoundary"/>, or
    /// <see cref="UnitBoundary.Empty"/> when no row exists or the
    /// boundary column is null.
    /// </summary>
    Task<UnitBoundary> GetBoundaryAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the unit's boundary in full. An empty boundary
    /// (<see cref="UnitBoundary.IsEmpty"/>) is represented as a null
    /// <c>boundary</c> column so the next read returns
    /// <see cref="UnitBoundary.Empty"/> through the column-absent path.
    /// </summary>
    Task SetBoundaryAsync(Guid unitId, UnitBoundary boundary, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the unit's persisted permission-inheritance flag,
    /// defaulting to <see cref="UnitPermissionInheritance.Inherit"/>
    /// when no row exists (ADR-0013: ancestor grants cascade by
    /// default).
    /// </summary>
    Task<UnitPermissionInheritance> GetPermissionInheritanceAsync(
        Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the unit's permission-inheritance flag. Always materialises
    /// (or updates) the row so the inheritance walk has a single SQL
    /// read regardless of whether the operator wrote
    /// <see cref="UnitPermissionInheritance.Inherit"/> or
    /// <see cref="UnitPermissionInheritance.Isolated"/>.
    /// </summary>
    Task SetPermissionInheritanceAsync(
        Guid unitId,
        UnitPermissionInheritance inheritance,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the configured own-expertise list for
    /// <paramref name="unitId"/> in the current tenant, sorted by
    /// domain name. Empty when no row exists or the unit has no
    /// own-expertise declared.
    /// </summary>
    Task<ExpertiseDomain[]> GetOwnExpertiseAsync(
        Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the unit's own-expertise list in full. Empty list is a
    /// legitimate "no own-expertise" state. Domains are deduplicated
    /// case-insensitively on <see cref="ExpertiseDomain.Name"/> with
    /// last-write-wins, and sorted by name. The
    /// <c>expertise_initialised</c> flag is flipped to <c>true</c> on
    /// every set call, even an empty list, so the activation seeder
    /// honours the actor-state-wins precedence rule (ADR-0040 / #488).
    /// </summary>
    Task<ExpertiseDomain[]> SetOwnExpertiseAsync(
        Guid unitId,
        IReadOnlyList<ExpertiseDomain> domains,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when an own-expertise list has been written
    /// for <paramref name="unitId"/> at all (even an empty list). Used
    /// by the activation seeder to honour the "actor state wins"
    /// precedence rule (ADR-0040 / #488) — once any value has been
    /// persisted, the YAML seed is not re-applied.
    /// </summary>
    Task<bool> HasOwnExpertiseSetAsync(
        Guid unitId, CancellationToken cancellationToken = default);
}
