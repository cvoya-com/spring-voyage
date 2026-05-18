// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using System.Text.Json;

using Cvoya.Spring.Connectors;

/// <summary>
/// Persistence abstraction for the <c>unit_connector_bindings</c> table
/// introduced in #2050 / ADR-0040. Encapsulates the
/// "at most one binding per <c>(tenant, unit)</c>" invariant so callers
/// (the singleton store, the future <c>ListConnectorBindings</c> read
/// path, …) agree on the schema without taking a direct dependency on
/// <see cref="SpringDbContext"/>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="IUnitLiveConfigRepository"/>: scoped registration,
/// EF-specific shapes (<see cref="JsonElement"/>) cross the interface,
/// implementations may be substituted via DI in cloud overlays.
/// </remarks>
public interface IUnitConnectorBindingRepository
{
    /// <summary>
    /// Returns the active binding for <paramref name="unitId"/> in the
    /// current tenant, or <c>null</c> when the unit is not bound. The
    /// returned record exposes only the connector-type id and the typed
    /// config — runtime metadata is read separately via
    /// <see cref="GetMetadataAsync"/>.
    /// </summary>
    Task<UnitConnectorBinding?> GetAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every active binding whose connector-type id equals
    /// <paramref name="connectorTypeId"/> in the current tenant scope,
    /// paired with the owning <c>unit_id</c>. Used by connectors that
    /// receive App / tenant-level deliveries with no unit pointer and
    /// must resolve the target unit from coordinates carried in the
    /// payload (issue #2456 — GitHub App-level delivery only).
    /// </summary>
    Task<IReadOnlyList<UnitConnectorBindingRow>> ListByConnectorTypeAsync(
        Guid connectorTypeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the unit's binding atomically. Replaces any prior binding
    /// regardless of connector type; the existing row is updated in
    /// place to preserve <c>id</c> stability for cross-table joins.
    /// Clearing the connector's typed config wipes the row's runtime
    /// metadata as well so a re-bind to a different connector type
    /// cannot leak the previous connector's metadata.
    /// </summary>
    Task SetAsync(
        Guid unitId,
        Guid connectorTypeId,
        JsonElement config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the unit's binding row if present. No-op when the unit
    /// is not bound. Also clears any persisted runtime metadata.
    /// </summary>
    Task ClearAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the connector-owned runtime metadata for
    /// <paramref name="unitId"/>, or <c>null</c> when the unit is not
    /// bound or the binding row has no metadata yet.
    /// </summary>
    Task<JsonElement?> GetMetadataAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists connector-owned runtime metadata on the existing binding
    /// row. Throws <see cref="InvalidOperationException"/> when the unit
    /// is not bound — runtime metadata is meaningless without a parent
    /// binding to teardown against.
    /// </summary>
    Task SetMetadataAsync(
        Guid unitId,
        JsonElement metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the connector-owned runtime metadata on the existing
    /// binding row. No-op when the unit is not bound.
    /// </summary>
    Task ClearMetadataAsync(Guid unitId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One row returned by
/// <see cref="IUnitConnectorBindingRepository.ListByConnectorTypeAsync"/> —
/// the owning unit id plus the binding payload.
/// </summary>
public sealed record UnitConnectorBindingRow(Guid UnitId, UnitConnectorBinding Binding);
