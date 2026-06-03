// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IHatReachabilityService"/>. Walks the three
/// membership tables (<c>unit_memberships_humans</c>,
/// <c>unit_memberships</c>, <c>unit_subunit_memberships</c>) to enforce the
/// v0.1 Hat ↔ unit reachability rule (#2972).
/// </summary>
/// <remarks>
/// <para>
/// Scoped because it holds a <see cref="SpringDbContext"/>; the DbContext's
/// tenant query filter restricts every read to the active tenant, so the
/// service never references <c>ITenantContext.CurrentTenantId</c> itself.
/// The OSS and cloud deployments share this implementation — the difference
/// is only which <c>TenantUser</c> id the caller arrives as. The cloud
/// overlay may register a tenant-aware decorator (or a configurable-policy
/// variant) via the <c>TryAdd*</c> seam.
/// </para>
/// </remarks>
public sealed class HatReachabilityService(SpringDbContext db) : IHatReachabilityService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetWearableHatsAsync(
        Guid tenantUserId,
        IReadOnlyCollection<Address> targets,
        CancellationToken cancellationToken = default)
    {
        if (tenantUserId == Guid.Empty)
        {
            throw new ArgumentException(
                "Caller TenantUser id must not be Guid.Empty.", nameof(tenantUserId));
        }

        ArgumentNullException.ThrowIfNull(targets);

        var boundHatIds = await db.Humans
            .AsNoTracking()
            .Where(h => h.TenantUserId == tenantUserId)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (boundHatIds.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        // Empty target set = "no constraint" (the unscoped listing case):
        // every bound Hat is wearable.
        if (targets.Count == 0)
        {
            return boundHatIds;
        }

        // Anchor units per Hat — the units each bound Hat is a DIRECT human
        // member of. A Hat with no anchor units (an orphaned Hat whose unit
        // was deleted before GC, or one that never had a membership) reaches
        // nothing and is excluded below.
        var boundSet = boundHatIds.ToHashSet();
        var anchorRows = await db.UnitMembershipsHumans
            .AsNoTracking()
            .Where(m => boundSet.Contains(m.HumanId))
            .Select(m => new { m.HumanId, m.UnitId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var anchorsByHat = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var row in anchorRows)
        {
            if (!anchorsByHat.TryGetValue(row.HumanId, out var set))
            {
                set = new HashSet<Guid>();
                anchorsByHat[row.HumanId] = set;
            }
            set.Add(row.UnitId);
        }

        // For each target, the set of anchor units from which a Hat reaches
        // it. A Hat is wearable for the whole send only when, for EVERY
        // target, at least one of its anchor units is in that target's
        // reachable-anchor set (intersection semantics for multi-recipient).
        var reachablePerTarget = new List<HashSet<Guid>>(targets.Count);
        foreach (var target in targets)
        {
            reachablePerTarget.Add(
                await ResolveReachableAnchorUnitsAsync(target, cancellationToken)
                    .ConfigureAwait(false));
        }

        var wearable = new List<Guid>(boundHatIds.Count);
        foreach (var hatId in boundHatIds)
        {
            if (!anchorsByHat.TryGetValue(hatId, out var hatAnchors) || hatAnchors.Count == 0)
            {
                continue;
            }

            var reachesAll = true;
            foreach (var reachable in reachablePerTarget)
            {
                if (!hatAnchors.Overlaps(reachable))
                {
                    reachesAll = false;
                    break;
                }
            }

            if (reachesAll)
            {
                wearable.Add(hatId);
            }
        }

        return wearable;
    }

    /// <inheritdoc />
    public async Task<bool> ReachesAsync(
        Guid humanId,
        Address target,
        CancellationToken cancellationToken = default)
    {
        if (humanId == Guid.Empty || target is null)
        {
            return false;
        }

        var reachable = await ResolveReachableAnchorUnitsAsync(target, cancellationToken)
            .ConfigureAwait(false);
        if (reachable.Count == 0)
        {
            return false;
        }

        return await db.UnitMembershipsHumans
            .AsNoTracking()
            .AnyAsync(
                m => m.HumanId == humanId && reachable.Contains(m.UnitId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The set of unit ids <c>U</c> from which <paramref name="target"/> is
    /// reachable — a Hat that is a direct human member of <c>U</c> reaches
    /// the target. That is every <c>U</c> where the target <em>is</em> the
    /// unit <c>U</c>, or the target is a direct member of <c>U</c> (agent,
    /// sub-unit, or co-member human). Empty for non-routable schemes.
    /// </summary>
    private async Task<HashSet<Guid>> ResolveReachableAnchorUnitsAsync(
        Address target,
        CancellationToken cancellationToken)
    {
        if (string.Equals(target.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            // The unit itself (a Hat in U reaches U), plus every parent unit
            // that lists U as a direct sub-unit member (a Hat in the parent
            // reaches U as a sibling sub-unit). For a top-level unit the
            // parent id is the tenant id, which never matches a real Hat
            // anchor — harmless to include.
            var unitId = target.Id;
            var parents = await db.UnitSubunitMemberships
                .AsNoTracking()
                .Where(m => m.ChildId == unitId)
                .Select(m => m.ParentId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var set = parents.ToHashSet();
            set.Add(unitId);
            return set;
        }

        if (string.Equals(target.Scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase))
        {
            // Every unit the agent is a direct member of. The Enabled flag is
            // about message processing within a unit, not membership, so it
            // does not affect reachability.
            var units = await db.UnitMemberships
                .AsNoTracking()
                .Where(m => m.AgentId == target.Id)
                .Select(m => m.UnitId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return units.ToHashSet();
        }

        if (string.Equals(target.Scheme, Address.HumanScheme, StringComparison.OrdinalIgnoreCase))
        {
            var units = await db.UnitMembershipsHumans
                .AsNoTracking()
                .Where(m => m.HumanId == target.Id)
                .Select(m => m.UnitId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return units.ToHashSet();
        }

        return new HashSet<Guid>();
    }
}
