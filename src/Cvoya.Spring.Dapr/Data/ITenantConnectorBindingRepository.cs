// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using System.Text.Json;

using Cvoya.Spring.Connectors;

/// <summary>
/// Persistence abstraction for the <c>tenant_connector_bindings</c>
/// table introduced by ADR-0061 §1. Encapsulates the "at most one
/// binding per <c>(tenant, connector_slug)</c>" invariant so callers
/// agree on the schema without taking a direct dependency on
/// <see cref="SpringDbContext"/>. Sibling to
/// <see cref="IUnitConnectorBindingRepository"/> — same shape, different
/// scope.
/// </summary>
/// <remarks>
/// ADR-0061 §7.7: the surface is generic, not Slack-specific. Future
/// tenant-scoped connectors (mailbox, calendar) reuse it without per-
/// connector storage code.
/// </remarks>
public interface ITenantConnectorBindingRepository
{
    /// <summary>
    /// Returns the active binding for <paramref name="connectorSlug"/>
    /// in the current tenant, or <c>null</c> when the tenant is not
    /// bound. Runtime metadata is read separately via
    /// <see cref="GetMetadataAsync"/>.
    /// </summary>
    Task<TenantConnectorBinding?> GetAsync(
        string connectorSlug,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the tenant's binding atomically. If the tenant was
    /// previously bound to the same slug, the existing row is updated
    /// in place to preserve <c>id</c> stability for cross-table joins.
    /// A re-bind wipes the row's runtime metadata so a fresh OAuth
    /// install cannot inherit the previous install's state (parallels
    /// the unit-binding repository's behaviour on rebind).
    /// </summary>
    Task SetAsync(
        string connectorSlug,
        Guid connectorTypeId,
        JsonElement config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the tenant's binding row for <paramref name="connectorSlug"/>
    /// if present. No-op when the tenant is not bound. Also clears any
    /// persisted runtime metadata.
    /// </summary>
    Task ClearAsync(
        string connectorSlug,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the connector-owned runtime metadata for
    /// <paramref name="connectorSlug"/>, or <c>null</c> when the tenant
    /// is not bound or the binding row has no metadata yet.
    /// </summary>
    Task<JsonElement?> GetMetadataAsync(
        string connectorSlug,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists connector-owned runtime metadata on the existing tenant
    /// binding row. Throws <see cref="InvalidOperationException"/> when
    /// the tenant is not bound.
    /// </summary>
    Task SetMetadataAsync(
        string connectorSlug,
        JsonElement metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the connector-owned runtime metadata on the existing
    /// tenant binding row. No-op when the tenant is not bound.
    /// </summary>
    Task ClearMetadataAsync(
        string connectorSlug,
        CancellationToken cancellationToken = default);
}
