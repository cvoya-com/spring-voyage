// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IInboxIdentityResolver"/>. Walks the FK on
/// <c>humans.tenant_user_id</c> introduced by ADR-0062 § 1 to map the
/// calling <c>TenantUser</c> to the set of <c>HumanEntity</c> ids the
/// inbox query should match recipient addresses against.
/// </summary>
/// <remarks>
/// <para>
/// Pre-ADR-0062 the OSS implementation returned every Human in the tenant
/// because the explicit binding was deferred to v0.2 — the OSS-default rule
/// "every Human maps to the single operator" was a derived projection.
/// ADR-0062 § 7 brings the FK forward and collapses the resolver to a
/// straight reverse-FK query that produces identical behaviour for both
/// OSS and cloud (the only difference is which <c>TenantUser.Id</c>
/// arrives in the caller address; the SELECT is the same).
/// </para>
/// <para>
/// The resolver is scoped because it holds a <see cref="SpringDbContext"/>;
/// the tenant query filter on the DbContext scopes the SELECT to the
/// active tenant, so the implementation itself never references
/// <c>ITenantContext.CurrentTenantId</c>.
/// </para>
/// <para>
/// Caller addresses other than <c>tenant-user://</c> are not in scope —
/// the cloud-overlay's per-tenant-user mapping was the original reason the
/// resolver existed as a DI seam, and ADR-0062 keeps the seam open so
/// future call-site shapes (a Human acting on its own behalf, a service
/// account) can register a decorator without forking the OSS default.
/// </para>
/// </remarks>
public sealed class InboxIdentityResolver(SpringDbContext db) : IInboxIdentityResolver
{
    /// <inheritdoc />
    public async Task<IReadOnlyCollection<Guid>> ResolveHumanIdsAsync(
        Address caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // Caller is expected to carry the tenant-user scheme post-#2768.
        // Other schemes (legacy paths, custom cloud-overlay shapes) fall
        // through to an empty result — the inbox shows nothing rather than
        // leaking another caller's Humans. The seam stays open for cloud
        // decorators that want to widen the resolver to non-tenant-user
        // shapes.
        if (!string.Equals(caller.Scheme, Address.TenantUserScheme, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<Guid>();
        }

        return await db.Humans
            .AsNoTracking()
            .Where(h => h.TenantUserId == caller.Id)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
