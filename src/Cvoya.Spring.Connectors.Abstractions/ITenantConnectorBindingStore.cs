// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

using System.Text.Json;

/// <summary>
/// Per-tenant connector binding store (ADR-0061 §1). Mirrors the
/// per-unit <c>IUnitConnectorBindingStore</c> but addresses
/// <c>(tenant, connector_slug)</c> instead of <c>(tenant, unit)</c>.
/// First consumer is the Slack connector; future workspace-shaped
/// connectors reuse it per ADR-0061 §7.7.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in
/// <c>Cvoya.Spring.Connectors.Abstractions</c> so connector packages
/// (which only reference Abstractions per CONVENTIONS §16) can talk
/// to it without taking a dependency on the EF / Dapr layer.
/// </para>
///
/// <para>
/// ADR-0061 §7.1: bound users are a list, even in OSS where the list
/// has length 1. The lookup <see cref="GetBoundUsersAsync"/> returns
/// a list of <see cref="TenantBoundUser"/> so callers iterate
/// uniformly across single-user and multi-user deployments. Slack's
/// OSS install pins one row; cloud may grow to many.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Returns the active binding addressed by
    /// <c>(connectorSlug, externalIdentity)</c> regardless of tenant.
    /// Backs the inbound-webhook routing path: a delivery arrives with
    /// only the connector-native identifier (e.g. the Slack
    /// <c>team_id</c>) and the platform needs to find which tenant
    /// binding owns it. The underlying index is unique on
    /// <c>(connector_slug, external_identity)</c>, so the result is
    /// either one binding or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The lookup intentionally bypasses the per-tenant query filter —
    /// the caller has no tenant context yet (that is the point of the
    /// call). Call sites must treat the result as cross-tenant and
    /// stamp the resolved tenant id before invoking any tenant-scoped
    /// surface.
    /// </remarks>
    Task<TenantConnectorBinding?> GetByExternalIdentityAsync(
        string connectorSlug,
        string externalIdentity,
        CancellationToken cancellationToken = default);

    /// <summary>Upserts the tenant's binding atomically.</summary>
    /// <param name="externalIdentity">
    /// Optional connector-native identifier (e.g. the Slack
    /// <c>team_id</c>) persisted on the row so an inbound webhook
    /// arriving with only that identifier can resolve back to this
    /// binding via <see cref="GetByExternalIdentityAsync"/>. The
    /// <c>(connector_slug, external_identity)</c> tuple is unique
    /// cross-tenant — two tenants cannot bind the same connector to
    /// the same external resource.
    /// </param>
    Task SetAsync(
        string connectorSlug,
        Guid connectorTypeId,
        JsonElement config,
        string? externalIdentity = null,
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
    /// <c>OssTenantUserIds.Operator</c>. Decoding is delegated to
    /// connector-supplied <see cref="ITenantBoundUserExtractor"/>
    /// implementations so the platform stays free of any
    /// connector-specific JSON schema knowledge.
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

/// <summary>
/// Connector-supplied decoder for the bound-user list embedded in a
/// tenant-binding row's opaque <c>Config</c> JSON. Each tenant-scoped
/// connector that has bound users registers one extractor with DI;
/// <see cref="ITenantConnectorBindingStore.GetBoundUsersAsync"/>
/// dispatches by slug. Keeps the storage layer free of any
/// connector-specific JSON schema knowledge (ADR-0061 §7.7).
/// </summary>
public interface ITenantBoundUserExtractor
{
    /// <summary>
    /// <c>true</c> when this extractor decodes bindings whose
    /// <c>connector_slug</c> equals <paramref name="connectorSlug"/>.
    /// </summary>
    bool Handles(string connectorSlug);

    /// <summary>
    /// Decodes the bound-user list from
    /// <paramref name="binding"/>'s <c>Config</c> payload. May return
    /// an empty list when no users are bound yet.
    /// </summary>
    IReadOnlyList<TenantBoundUser> Extract(TenantConnectorBinding binding);
}
