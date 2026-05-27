// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Messaging;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default <see cref="ITenantUserHumanResolver"/> implementation. Walks the
/// FK on <c>humans.tenant_user_id</c> introduced by ADR-0062 § 1 to pick
/// the routable <c>human://</c> sender Address for an outbound message.
/// </summary>
/// <remarks>
/// <para>
/// The implementation is structurally identical for OSS and cloud — both
/// deployments populate the FK at insert time through
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantUserDefaultResolver"/>, and
/// the read path is the same reverse-FK query. ADR-0062 § 7 collapses the
/// previously OSS-specific identity surfaces (the inbox resolver also
/// loses its OSS prefix) for the same reason.
/// </para>
/// <para>
/// Lifetime is scoped because it holds a <see cref="SpringDbContext"/>;
/// the DbContext's tenant query filter restricts the SELECT to the active
/// tenant, so the resolver itself never references
/// <c>ITenantContext.CurrentTenantId</c>.
/// </para>
/// </remarks>
public sealed class TenantUserHumanResolver(SpringDbContext db) : ITenantUserHumanResolver
{
    /// <inheritdoc />
    public async Task<Address> PickFromAsync(
        Guid callerTenantUserId,
        Guid? explicitFromHumanId,
        Guid? threadId,
        CancellationToken cancellationToken = default)
    {
        if (callerTenantUserId == Guid.Empty)
        {
            throw new ArgumentException(
                "Caller TenantUser id must not be Guid.Empty.", nameof(callerTenantUserId));
        }

        // 1. Explicit override wins, after membership validation. The
        //    bound-set check is the load-bearing invariant — it stops a
        //    caller from impersonating another TenantUser's Hat by
        //    forging a `from` field on the request DTO.
        if (explicitFromHumanId is { } explicitId)
        {
            var bound = await db.Humans
                .AsNoTracking()
                .AnyAsync(
                    h => h.Id == explicitId && h.TenantUserId == callerTenantUserId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!bound)
            {
                throw new NoBoundHumanException(
                    $"Human '{explicitId:D}' is not bound to the calling TenantUser.");
            }

            return new Address(Address.HumanScheme, explicitId);
        }

        // 2. Thread-pinned Hat (reply default). Inspect the most recent
        //    message on the thread whose recipient is in the caller's
        //    bound Human set; reply as that Hat. Skipped when there is
        //    no thread context (new outbound from a composer launched
        //    outside a thread).
        if (threadId is { } resolvedThreadId && resolvedThreadId != Guid.Empty)
        {
            var threadHat = await ResolveThreadPinnedHatAsync(
                callerTenantUserId,
                resolvedThreadId,
                cancellationToken)
                .ConfigureAwait(false);

            if (threadHat is { } hat)
            {
                return new Address(Address.HumanScheme, hat);
            }
        }

        // 3. TenantUser.PrimaryHumanId — the operator-pinned default Hat
        //    for new outbound from a composer with no thread context.
        var primary = await db.TenantUsers
            .AsNoTracking()
            .Where(u => u.Id == callerTenantUserId)
            .Select(u => u.PrimaryHumanId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (primary is { } primaryId && primaryId != Guid.Empty)
        {
            return new Address(Address.HumanScheme, primaryId);
        }

        // 4. Last-chance fallback for the caller-has-bindings-but-no-primary
        //    case (e.g. the OSS seeded operator + a freshly-installed
        //    package's Human before the primary is repinned). Pick any
        //    one bound Human so the send still flows — the operator can
        //    explicitly switch hats afterwards. ADR-0062 § 3 keeps the
        //    "or 400 NoBoundHuman" branch for the truly unbound case.
        var anyBound = await db.Humans
            .AsNoTracking()
            .Where(h => h.TenantUserId == callerTenantUserId)
            .Select(h => h.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (anyBound != Guid.Empty)
        {
            return new Address(Address.HumanScheme, anyBound);
        }

        throw new NoBoundHumanException(
            $"TenantUser '{callerTenantUserId:D}' has no bound Human to send as.");
    }

    /// <summary>
    /// Looks up the most recent message on <paramref name="threadId"/>
    /// whose recipient is a Human bound to <paramref name="callerTenantUserId"/>
    /// and returns that Human id. This is the "reply as the Hat you were
    /// addressed as" default from ADR-0062 § 5. Returns null when no
    /// inbound on the thread matches (the caller never received on this
    /// thread under any of their hats).
    /// </summary>
    private async Task<Guid?> ResolveThreadPinnedHatAsync(
        Guid callerTenantUserId,
        Guid threadId,
        CancellationToken cancellationToken)
    {
        var boundHumanIds = await db.Humans
            .AsNoTracking()
            .Where(h => h.TenantUserId == callerTenantUserId)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (boundHumanIds.Count == 0)
        {
            return null;
        }

        var lastReceivedHumanId = await db.Messages
            .AsNoTracking()
            .Where(m => m.ThreadId == threadId
                && m.RecipientScheme == Address.HumanScheme
                && boundHumanIds.Contains(m.RecipientId))
            .OrderByDescending(m => m.SentAt)
            .Select(m => (Guid?)m.RecipientId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return lastReceivedHumanId;
    }
}
