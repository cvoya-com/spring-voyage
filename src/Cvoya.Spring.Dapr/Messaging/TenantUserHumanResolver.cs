// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Messaging;

using Cvoya.Spring.Core.Identifiers;
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
public sealed class TenantUserHumanResolver(SpringDbContext db, IThreadRegistry threadRegistry) : ITenantUserHumanResolver
{
    /// <inheritdoc />
    public Task<Address> PickFromAsync(
        Guid callerTenantUserId,
        Guid? explicitFromHumanId,
        Guid? threadId,
        CancellationToken cancellationToken = default)
        => PickFromAsync(
            callerTenantUserId,
            explicitFromHumanId,
            threadId,
            allowedHumanIds: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<Address> PickFromAsync(
        Guid callerTenantUserId,
        Guid? explicitFromHumanId,
        Guid? threadId,
        IReadOnlyCollection<Guid>? allowedHumanIds,
        CancellationToken cancellationToken = default)
    {
        if (callerTenantUserId == Guid.Empty)
        {
            throw new ArgumentException(
                "Caller TenantUser id must not be Guid.Empty.", nameof(callerTenantUserId));
        }

        // #2972: when the endpoint computed a reachability-constrained set,
        // every resolution branch below is restricted to it so the stamped
        // sender is always a Hat the caller may address the target with.
        // Null means "no constraint" (the gate does not apply here).
        var allowed = allowedHumanIds is null ? null : new HashSet<Guid>(allowedHumanIds);

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

            // #2972: the chosen Hat is bound but cannot reach the target —
            // a distinct failure from "no wearable Hat at all" so the caller
            // learns to pick a different Hat rather than that they are
            // wholly unable to message the target.
            if (allowed is not null && !allowed.Contains(explicitId))
            {
                throw new HatNotReachableException(
                    $"Human '{explicitId:D}' is bound to the calling TenantUser but cannot reach the message target(s).");
            }

            return new Address(Address.HumanScheme, explicitId);
        }

        // 2. Thread-participant Hat (reply default — ADR-0062 § 5,
        //    generalised per #2865). Pin to the caller's bound Hat that
        //    is a canonical participant of the thread. This catches both
        //    "received as X" (the inbound named X as recipient) and
        //    "originated as X" (the caller is X and started the thread)
        //    in one shot — both make X a thread participant, which is
        //    the invariant ADR-0030 names. The old "most recent inbound
        //    recipient" lookup was a special case; intersecting with the
        //    canonical participant set covers it and the originated-as
        //    case the special case missed.
        //
        //    Returning null here (e.g. the thread row is unknown, or
        //    none of the caller's bound Hats is a participant) lets the
        //    later branches choose PrimaryHumanId. The endpoint-level
        //    SenderNotInThread gate (#2865) catches the case where that
        //    primary falls outside the canonical set, so corrupt thread
        //    routing cannot silently land here.
        if (threadId is { } resolvedThreadId && resolvedThreadId != Guid.Empty)
        {
            var threadHat = await ResolveThreadParticipantHatAsync(
                callerTenantUserId,
                resolvedThreadId,
                allowed,
                cancellationToken)
                .ConfigureAwait(false);

            if (threadHat is { } hat)
            {
                return new Address(Address.HumanScheme, hat);
            }
        }

        // 3. TenantUser.PrimaryHumanId — the operator-pinned default Hat
        //    for new outbound from a composer with no thread context. Under
        //    a reachability constraint (#2972) the primary only wins when it
        //    can reach the target; otherwise fall through to a wearable Hat.
        var primary = await db.TenantUsers
            .AsNoTracking()
            .Where(u => u.Id == callerTenantUserId)
            .Select(u => u.PrimaryHumanId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (primary is { } primaryId && primaryId != Guid.Empty
            && (allowed is null || allowed.Contains(primaryId)))
        {
            return new Address(Address.HumanScheme, primaryId);
        }

        // 4. Last-chance fallback for the caller-has-bindings-but-no-primary
        //    case (e.g. the OSS seeded operator + a freshly-installed
        //    package's Human before the primary is repinned). Under a
        //    reachability constraint pick the lowest-Guid wearable Hat — the
        //    set is non-empty by construction (the endpoint rejects an empty
        //    wearable set with NoReachableHat before calling). Without a
        //    constraint, pick any one bound Human so the send still flows.
        //    ADR-0062 § 3 keeps the "or 400 NoBoundHuman" branch for the
        //    truly unbound case.
        if (allowed is not null)
        {
            if (allowed.Count > 0)
            {
                return new Address(Address.HumanScheme, allowed.OrderBy(id => id).First());
            }

            throw new NoBoundHumanException(
                $"TenantUser '{callerTenantUserId:D}' has no Hat that can reach the message target(s).");
        }

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
    /// Returns the caller's bound Hat that is a canonical participant of
    /// <paramref name="threadId"/> (ADR-0030's identity invariant intersected
    /// with the caller's bound set). When multiple bound Hats are
    /// participants — multi-Hat threads where the same TenantUser wears
    /// two hats — falls back to the most recent received-as Hat (ADR-0062
    /// § 5), then the most recent originated-as Hat, then the lowest-Guid
    /// participant as a deterministic tie-break. Returns null when no
    /// thread row exists, or none of the caller's bound Hats is in the
    /// canonical participant set.
    /// </summary>
    private async Task<Guid?> ResolveThreadParticipantHatAsync(
        Guid callerTenantUserId,
        Guid threadId,
        HashSet<Guid>? allowed,
        CancellationToken cancellationToken)
    {
        var boundHumanIds = await db.Humans
            .AsNoTracking()
            .Where(h => h.TenantUserId == callerTenantUserId)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // #2972: under a reachability constraint only Hats that can reach
        // the target are eligible to be the thread reply Hat. Intersecting
        // here (rather than rejecting after the pick) keeps a wearable
        // thread participant from being skipped in favour of a non-wearable
        // most-recent one on a multi-Hat thread.
        if (allowed is not null)
        {
            boundHumanIds = boundHumanIds.Where(allowed.Contains).ToList();
        }

        if (boundHumanIds.Count == 0)
        {
            return null;
        }

        var entry = await threadRegistry
            .ResolveAsync(GuidFormatter.Format(threadId), cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        var boundSet = new HashSet<Guid>(boundHumanIds);
        var eligible = entry.Participants
            .Where(p => string.Equals(p.Scheme, Address.HumanScheme, StringComparison.OrdinalIgnoreCase)
                        && boundSet.Contains(p.Id))
            .Select(p => p.Id)
            .ToList();

        if (eligible.Count == 0)
        {
            return null;
        }
        if (eligible.Count == 1)
        {
            return eligible[0];
        }

        // Multi-Hat case: ADR-0062 § 5 says "reply as the Hat that received
        // the inbound." Look for the most recent message whose recipient
        // is one of the eligible Hats.
        var eligibleSet = new HashSet<Guid>(eligible);
        var lastReceived = await db.Messages
            .AsNoTracking()
            .Where(m => m.ThreadId == threadId
                && m.RecipientScheme == Address.HumanScheme
                && eligibleSet.Contains(m.RecipientId))
            .OrderByDescending(m => m.SentAt)
            .Select(m => (Guid?)m.RecipientId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lastReceived is { } recv)
        {
            return recv;
        }

        // Originated-as fallback: the caller started the thread but has
        // not yet received on it under any Hat. Pick the Hat that last
        // sent on the thread.
        var lastSent = await db.Messages
            .AsNoTracking()
            .Where(m => m.ThreadId == threadId
                && m.SenderScheme == Address.HumanScheme
                && eligibleSet.Contains(m.SenderId))
            .OrderByDescending(m => m.SentAt)
            .Select(m => (Guid?)m.SenderId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (lastSent is { } sent)
        {
            return sent;
        }

        // Empty-thread tie-break (thread row exists but has no messages
        // yet — possible during in-flight thread creation): lowest-Guid
        // wins so the choice is deterministic across racing callers.
        return eligible.OrderBy(id => id).First();
    }
}
