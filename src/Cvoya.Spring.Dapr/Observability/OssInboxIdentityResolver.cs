// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// OSS-default <see cref="IInboxIdentityResolver"/>. Returns every
/// <c>HumanEntity</c> id in the current tenant — the OSS deployment ships
/// with exactly one operator <c>TenantUser</c>, and per
/// <see cref="Cvoya.Spring.Core.Tenancy.OssTenantUserIds"/> and ADR-0047
/// §7 the implicit mapping rule is "every Human in the tenant maps to
/// that single operator." Cloud overlays register a tenant-aware variant
/// via <c>TryAddScoped</c> that walks the explicit
/// <c>Human → TenantUser</c> mapping rows.
/// </summary>
/// <remarks>
/// <para>
/// The resolver is scoped because it holds a <see cref="SpringDbContext"/>;
/// the tenant query filter on the DbContext scopes the SELECT to the
/// active tenant per CONVENTIONS § 12, so the implementation itself
/// never references <c>ITenantContext.CurrentTenantId</c>.
/// </para>
/// <para>
/// The contract intentionally returns even the caller-side "fan-in
/// includes me" set without filtering — the OSS rule is uniform and
/// returning all Human ids preserves the property that a message
/// addressed to any role-slot Human surfaces on the operator's inbox
/// (#2766).
/// </para>
/// </remarks>
internal sealed class OssInboxIdentityResolver(SpringDbContext db) : IInboxIdentityResolver
{
    /// <inheritdoc />
    public async Task<IReadOnlyCollection<Guid>> ResolveHumanIdsAsync(
        Address caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // The OSS rule does not distinguish between tenant-user and human
        // callers — a human caller (a package-declared role-slot acting on
        // its own behalf) still resolves to the full Human set, because
        // every Human row maps to the single operator. The cloud overlay's
        // override is where per-tenant-user mapping logic lives.
        return await db.Humans
            .AsNoTracking()
            .Select(h => h.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
