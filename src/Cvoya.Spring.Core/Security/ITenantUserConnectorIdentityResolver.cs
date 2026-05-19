// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Security;

/// <summary>
/// Two-way resolver between a stable <c>TenantUser</c> UUID and a
/// connector-native user identifier (e.g. a GitHub login). Implementations
/// consult the <c>tenant_user_connector_identities</c> table per
/// ADR-0047 §2.
/// </summary>
/// <remarks>
/// <para>
/// Display-side only. The resolver answers "who is this SV tenant user in
/// connector X terms?" and "given a connector login, which SV tenant user
/// claimed it in this tenant?" Outbound auth (PATs, App-installation
/// tokens) lives on the unit binding row per ADR-0047 §§ 5–6 and is
/// resolved through a separate seam — there is no auth context on this
/// interface.
/// </para>
/// <para>
/// Implementations MUST honour the tenant query filter: a lookup that
/// would cross the tenant boundary returns <c>null</c>. Per ADR-0047 §1
/// the same human authenticated against two tenants produces two
/// distinct <c>TenantUser</c> rows; cross-tenant identity sharing is
/// explicitly not a thing.
/// </para>
/// <para>
/// The OSS default implementation in <c>Cvoya.Spring.Dapr</c> is registered
/// <c>TryAddScoped</c> so the hosted overlay can substitute a decorating
/// implementation.
/// </para>
/// </remarks>
public interface ITenantUserConnectorIdentityResolver
{
    /// <summary>
    /// Returns the tenant user a connector identity points at, or
    /// <c>null</c> when no row maps the supplied login to a tenant user
    /// in the current tenant. Used by inbound paths that arrive with a
    /// connector-native id (e.g. a webhook payload's reviewer login) and
    /// need to address the tenant user in platform-native form.
    /// </summary>
    /// <param name="connectorId">
    /// The connector slug (matches <c>IConnectorType.Slug</c>, e.g.
    /// <c>github</c>).
    /// </param>
    /// <param name="username">
    /// The connector-native user identifier — for GitHub this is the
    /// login string without the leading <c>@</c>.
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<TenantUserConnectorIdentity?> ResolveTenantUserByUsernameAsync(
        string connectorId,
        string username,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the connector-native username a tenant user is mapped to
    /// on the supplied connector, or <c>null</c> when no row exists. Used
    /// by outbound paths that hold a <c>TenantUser</c> UUID (because a
    /// workflow / <c>sv.*</c> tool resolved it) and need the connector-
    /// native id to call the remote API.
    /// </summary>
    /// <param name="tenantUserId">The stable <c>TenantUser</c> UUID.</param>
    /// <param name="connectorId">The connector slug.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<string?> GetUsernameAsync(
        Guid tenantUserId,
        string connectorId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// DTO returned by
/// <see cref="ITenantUserConnectorIdentityResolver.ResolveTenantUserByUsernameAsync"/>.
/// Carries the resolved tenant user's identity alongside the matched
/// mapping row's display fields so a caller can render the result without
/// a second DB round-trip.
/// </summary>
/// <param name="TenantUserId">The stable <c>TenantUser</c> UUID the identity maps to.</param>
/// <param name="ConnectorId">The connector slug (e.g. <c>github</c>).</param>
/// <param name="Username">The connector-native username (e.g. a GitHub login).</param>
/// <param name="DisplayHandle">
/// Optional human-readable label stored alongside the row; <c>null</c>
/// when the operator did not supply one. Falls back to <see cref="Username"/>
/// for render purposes.
/// </param>
public sealed record TenantUserConnectorIdentity(
    Guid TenantUserId,
    string ConnectorId,
    string Username,
    string? DisplayHandle);
