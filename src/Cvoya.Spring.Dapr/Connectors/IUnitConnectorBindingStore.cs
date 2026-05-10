// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using System.Text.Json;

using Cvoya.Spring.Connectors;

/// <summary>
/// Singleton seam over the scoped <c>IUnitConnectorBindingRepository</c>.
/// Mirrors <c>IUnitLiveConfigStore</c>: hosts that consume the
/// connector-binding surface from singleton-style call sites
/// (<c>UnitActorConnectorConfigStore</c>, <c>DispatchConnectorStartAsync</c>
/// in <c>UnitEndpoints</c>, …) cannot resolve the scoped EF repository
/// directly. The store creates a fresh DI scope per call, resolves the
/// repository, and forwards the operation.
/// </summary>
/// <remarks>
/// Registered as <c>TryAddSingleton</c> so cloud overlays can layer
/// audit logging or cross-tenant guards on top without displacing the
/// OSS default. Activation-path reads are instrumented with a
/// <see cref="System.Diagnostics.Stopwatch"/> + <c>LogDebug</c> so the
/// v0.2 cache decision is data-driven (ADR-0040 § 3).
/// </remarks>
public interface IUnitConnectorBindingStore
{
    /// <summary>
    /// Returns the active binding for <paramref name="unitId"/> in the
    /// current tenant, or <c>null</c> when the unit is not bound.
    /// </summary>
    Task<UnitConnectorBinding?> GetAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>Upserts the unit's binding atomically.</summary>
    Task SetAsync(
        Guid unitId,
        Guid connectorTypeId,
        JsonElement config,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the unit's binding row if present.</summary>
    Task ClearAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the connector-owned runtime metadata for
    /// <paramref name="unitId"/>, or <c>null</c> when no binding exists
    /// or the binding has no metadata yet.
    /// </summary>
    Task<JsonElement?> GetMetadataAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists connector-owned runtime metadata on the existing binding
    /// row. The repository throws when the unit is not bound.
    /// </summary>
    Task SetMetadataAsync(
        Guid unitId,
        JsonElement metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the connector-owned runtime metadata. No-op when the unit
    /// is not bound.
    /// </summary>
    Task ClearMetadataAsync(Guid unitId, CancellationToken cancellationToken = default);
}
