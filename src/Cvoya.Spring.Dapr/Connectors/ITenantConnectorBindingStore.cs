// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using System.Text.Json;

using Cvoya.Spring.Connectors;

/// <summary>
/// Singleton seam over the scoped <c>ITenantConnectorBindingRepository</c>.
/// Mirrors <see cref="IUnitConnectorBindingStore"/>: hosts that consume
/// the tenant-binding surface from singleton-style call sites cannot
/// resolve the scoped EF repository directly. The store creates a fresh
/// DI scope per call, resolves the repository, and forwards the
/// operation.
///
/// <para>
/// Introduced by ADR-0061 §1 alongside the
/// <c>tenant_connector_bindings</c> table. Generic per ADR-0061 §7.7 —
/// the surface holds no Slack-specific shapes; the first consumer is
/// the Slack connector (ADR-0061 §2) and future workspace-shaped
/// connectors reuse the same store.
/// </para>
///
/// <para>
/// ADR-0061 §7.1: bound users are a list, even in OSS where the list
/// has length 1. The lookup
/// <see cref="GetBoundUsersAsync"/> returns a list of
/// <c>(external_user_id, tenant_user_id)</c> mappings so callers
/// iterate uniformly across single-user and multi-user deployments.
/// </para>
/// </summary>
public interface ITenantConnectorBindingStore
{
    /// <summary>
    /// Returns the active binding for <paramref name="connectorSlug"/>
    /// in the current tenant, or <c>null</c> when the tenant is not
    /// bound.
    /// </summary>
    Task<TenantConnectorBinding?> GetAsync(
        string connectorSlug,
        CancellationToken cancellationToken = default);

    /// <summary>Upserts the tenant's binding atomically.</summary>
    Task SetAsync(
        string connectorSlug,
        Guid connectorTypeId,
        JsonElement config,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the tenant's binding row if present.</summary>
    Task ClearAsync(
        string connectorSlug,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the connector-owned runtime metadata for
    /// <paramref name="connectorSlug"/>, or <c>null</c> when the
    /// tenant is not bound or the binding has no metadata yet.
    /// </summary>
    Task<JsonElement?> GetMetadataAsync(
        string connectorSlug,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists connector-owned runtime metadata on the existing
    /// binding row. The repository throws when the tenant is not bound.
    /// </summary>
    Task SetMetadataAsync(
        string connectorSlug,
        JsonElement metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the connector-owned runtime metadata. No-op when the
    /// tenant is not bound.
    /// </summary>
    Task ClearMetadataAsync(
        string connectorSlug,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the set of users bound to <paramref name="connectorSlug"/>
    /// in the current tenant — <c>(external_user_id, tenant_user_id)</c>
    /// pairs. ADR-0061 §7.1: the result is a list (length 1 in OSS,
    /// length N in cloud) so iterating call sites do not branch on
    /// deployment shape.
    /// </summary>
    /// <remarks>
    /// The <c>external_user_id</c> is the connector-native identifier
    /// (for Slack: the workspace <c>user_id</c>). The
    /// <c>tenant_user_id</c> is the SV-side TenantUser this external
    /// user is mapped to — in OSS the single
    /// <c>OssTenantUserIds.Operator</c>. The OSS default implementation
    /// reads the bound-user list from the connector's binding config
    /// (Slack stores it on the binding row); cloud overlays can layer
    /// a dedicated table if the list grows past a few entries.
    /// </remarks>
    Task<IReadOnlyList<TenantBoundUser>> GetBoundUsersAsync(
        string connectorSlug,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One row returned by
/// <see cref="ITenantConnectorBindingStore.GetBoundUsersAsync"/>.
/// </summary>
/// <param name="ExternalUserId">
/// The connector-native user identifier (e.g. the Slack
/// <c>user_id</c>). Opaque to the platform.
/// </param>
/// <param name="TenantUserId">
/// The SV-side <c>TenantUser</c> this external user is mapped to.
/// </param>
public sealed record TenantBoundUser(string ExternalUserId, Guid TenantUserId);
