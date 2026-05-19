// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Security;

/// <summary>
/// Write seam for the per-<c>TenantUser</c> connector display-identity
/// rows (ADR-0047 §2). Complements
/// <see cref="ITenantUserConnectorIdentityResolver"/>'s read side so
/// non-API surfaces (the OAuth callback per ADR-0047 §13, package install
/// hooks, future cloud-side provisioning) can upsert / delete an identity
/// without round-tripping through HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <b>Display-side only.</b> Same shape as the resolver — no PAT, no
/// installation override, no auth fields. Outbound credentials live on
/// the unit binding row per ADR-0047 §§ 5–6.
/// </para>
/// <para>
/// <b>Tenant scoping.</b> Implementations honour the ambient
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantContext"/> exactly the
/// resolver does: an upsert that would address a cross-tenant
/// <c>tenant_user_id</c> writes nothing and surfaces the same
/// "tenant user not found" outcome as the API endpoint's 404. The
/// caller never specifies a tenant id explicitly.
/// </para>
/// <para>
/// <b>Idempotency.</b> <see cref="UpsertAsync"/> is the natural-key
/// upsert from <c>TenantUserIdentityEndpoints.UpsertIdentityAsync</c>:
/// a row keyed on <c>(tenant, tenant_user, connector)</c> is replaced
/// in place if it already exists. A cross-row login collision
/// (another tenant user already claims the same connector login)
/// surfaces as <see cref="TenantUserConnectorIdentityUpsertOutcome.LoginAlreadyClaimed"/>
/// so callers can decide whether to keep the OAuth session alive
/// without an identity update.
/// </para>
/// <para>
/// The OSS default implementation in <c>Cvoya.Spring.Dapr</c> is
/// registered <c>TryAddScoped</c> so the hosted overlay can substitute a
/// decorating implementation.
/// </para>
/// </remarks>
public interface ITenantUserConnectorIdentityWriter
{
    /// <summary>
    /// Upserts the <c>(tenant, tenant_user, connector)</c> identity row
    /// to the supplied <paramref name="username"/> /
    /// <paramref name="displayHandle"/>. Returns an outcome the caller
    /// can branch on; the writer never throws on application-level
    /// conditions (unknown tenant user, login claimed by a different
    /// tenant user) — both surface as a non-success outcome.
    /// </summary>
    /// <param name="tenantUserId">The tenant user the identity belongs to.</param>
    /// <param name="connectorId">The connector slug (e.g. <c>github</c>).</param>
    /// <param name="username">The connector-native login (no leading <c>@</c>).</param>
    /// <param name="displayHandle">
    /// Optional human-friendly rendering. <c>null</c> falls back to
    /// <paramref name="username"/> at render time.
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<TenantUserConnectorIdentityUpsertOutcome> UpsertAsync(
        Guid tenantUserId,
        string connectorId,
        string username,
        string? displayHandle,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a
/// <see cref="ITenantUserConnectorIdentityWriter.UpsertAsync"/> call.
/// Mirrors the wire envelope <c>TenantUserIdentityEndpoints</c> emits so
/// non-HTTP callers can branch without parsing problem-details.
/// </summary>
public enum TenantUserConnectorIdentityUpsertOutcome
{
    /// <summary>
    /// The row was created or updated in place. <c>200 OK</c> wire
    /// equivalent.
    /// </summary>
    Upserted = 0,

    /// <summary>
    /// The <paramref name="tenantUserId"/> does not exist in the current
    /// tenant. <c>404 Not Found</c> wire equivalent.
    /// </summary>
    TenantUserNotFound = 1,

    /// <summary>
    /// Another tenant user in the same tenant already claims the supplied
    /// <c>(connector, username)</c> pair. <c>409 Conflict</c> wire
    /// equivalent. The caller keeps the OAuth flow's other side effects
    /// (e.g. the secret-write) and surfaces a soft warning to the
    /// operator.
    /// </summary>
    LoginAlreadyClaimed = 2,
}
