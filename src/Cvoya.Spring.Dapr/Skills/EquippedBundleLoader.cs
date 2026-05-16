// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Collections.Generic;
using System.Globalization;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Resolves the unit-scoped (Layer 2) and agent-scoped (Layer 4) skill
/// bundles that should feed an agent actor's prompt-assembly context at
/// dispatch time (#2360 + #2363).
/// </summary>
/// <remarks>
/// <para>
/// The agent-scoped read is direct: <see cref="IAgentSkillBundleStore"/>
/// keyed by the actor's own id, feeding Layer 4.
/// </para>
/// <para>
/// The unit-scoped read covers two cases:
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Unit-as-agent (ADR-0039).</b> When the actor itself is a
///       unit, <see cref="IUnitSkillBundleStore"/> is keyed by the unit's
///       id which is the actor id. The direct read returns the unit's
///       own equipped bundles. This is the existing behaviour wired in
///       <see href="https://github.com/cvoya-com/spring-voyage/issues/2360">#2360</see>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Leaf agent → parent unit inheritance (#2363).</b> When the
///       actor is a leaf agent, its own actor id is never a key in the
///       unit store. The bundles equipped on each parent unit live under
///       the parent unit's actor id, reachable via
///       <see cref="IUnitMembershipRepository.ListByAgentAsync"/>. This
///       helper walks the multi-parent list and concatenates every
///       parent's bundles into Layer 2.
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Dedup.</b> Multi-parent unions can introduce the same
/// <c>(PackageName, SkillName)</c> pair twice. The first occurrence
/// wins; subsequent duplicates are silently dropped. The agent's own
/// unit-store entry (the unit-as-agent case) is processed first so a
/// unit's own bundles always win over an inherited copy.
/// </para>
/// <para>
/// <b>Tie-break order.</b> Memberships returned by
/// <see cref="IUnitMembershipRepository.ListByAgentAsync"/> are already
/// in stable <c>CreatedAt</c> order, but that's an internal-mutation
/// timestamp the operator can't reason about. For multi-parent
/// inheritance we instead sort the parent units alphabetically by
/// <see cref="Data.Entities.UnitDefinitionEntity.DisplayName"/>
/// (ordinal, case-insensitive). The display name is the only label an
/// operator sees in the portal / CLI, so when two parents both equip
/// distinct skills the assembled prompt's section order matches what
/// they read on screen.
/// </para>
/// <para>
/// Cascading inheritance through nested units (sub-unit → parent unit)
/// is out of scope for v0.1 per <see cref="IUnitSubunitMembershipRepository"/>.
/// This helper does not consult the unit-subunit edge.
/// </para>
/// </remarks>
internal static class EquippedBundleLoader
{
    /// <summary>
    /// Loads the (Layer 2, Layer 4) bundle pair for the actor identified
    /// by <paramref name="actorId"/>. Returns <c>(null, null)</c> when
    /// <paramref name="scopeFactory"/> is <c>null</c> so legacy test
    /// compositions that construct <c>AgentActor</c> without DI degrade
    /// cleanly to the no-bundle render.
    /// </summary>
    /// <param name="scopeFactory">
    /// Root scope factory. The loader opens one short-lived async scope
    /// per call; every store / repository read happens inside that scope
    /// because <see cref="IUnitSkillBundleStore"/> /
    /// <see cref="IAgentSkillBundleStore"/> /
    /// <see cref="IUnitMembershipRepository"/> are all scoped DI services.
    /// </param>
    /// <param name="actorId">
    /// The actor's id in canonical wire form
    /// (<see cref="Cvoya.Spring.Core.Identifiers.GuidFormatter.Format"/>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<(IReadOnlyList<SkillBundle>? Unit, IReadOnlyList<SkillBundle>? Agent)>
        LoadAsync(
            IServiceScopeFactory? scopeFactory,
            string actorId,
            CancellationToken cancellationToken)
    {
        if (scopeFactory is null)
        {
            return (null, null);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var unitStore = sp.GetService<IUnitSkillBundleStore>();
        var agentStore = sp.GetService<IAgentSkillBundleStore>();

        // Agent (Layer 4) — direct keyed read. Untouched from #2360.
        var agentBundles = agentStore is null
            ? null
            : await agentStore.GetAsync(actorId, cancellationToken).ConfigureAwait(false);

        // Unit (Layer 2) — direct read of the actor's own id (covers the
        // unit-as-agent case from ADR-0039) plus the leaf-agent → parent
        // walk (#2363).
        IReadOnlyList<SkillBundle>? unitBundles = null;
        if (unitStore is not null)
        {
            unitBundles = await LoadUnitBundlesWithInheritanceAsync(
                sp,
                unitStore,
                actorId,
                cancellationToken).ConfigureAwait(false);
        }

        return (
            unitBundles is { Count: > 0 } ? unitBundles : null,
            agentBundles is { Count: > 0 } ? agentBundles : null);
    }

    /// <summary>
    /// Builds the unit-store contribution to Layer 2: the actor's own
    /// keyed entry (for unit-as-agent) plus, for leaf agents, each
    /// parent unit's bundles in display-name order. Dedups on
    /// <c>(PackageName, SkillName)</c> with first-occurrence wins.
    /// </summary>
    private static async Task<IReadOnlyList<SkillBundle>> LoadUnitBundlesWithInheritanceAsync(
        IServiceProvider sp,
        IUnitSkillBundleStore unitStore,
        string actorId,
        CancellationToken cancellationToken)
    {
        // Stash the in-order result and a side-set for the dedup gate.
        // Ordinal comparers are intentional: package + skill identifiers
        // are case-sensitive across the platform (slug-shaped).
        var result = new List<SkillBundle>();
        var seen = new HashSet<(string Package, string Skill)>();

        // (1) Direct keyed read. For a unit-as-agent this returns the
        //     unit's own bundles; for a leaf agent it's empty (the agent's
        //     actor id is never a key in the unit store). Doing this
        //     first guarantees "direct wins" on dedup — an inherited
        //     copy never overrides the unit's own entry.
        var directBundles = await unitStore.GetAsync(actorId, cancellationToken).ConfigureAwait(false);
        if (directBundles is { Count: > 0 })
        {
            foreach (var bundle in directBundles)
            {
                AppendIfNew(bundle, result, seen);
            }
        }

        // (2) Membership walk. Resolve the actor's parent units via the
        //     same repository the connector-grant inheritance walk uses.
        //     A unit subject has no membership rows here (the table is
        //     agent → unit), so ListByAgentAsync(unitId) returns empty
        //     and this branch is a no-op for the unit-as-agent path.
        var memberships = sp.GetService<IUnitMembershipRepository>();
        if (memberships is null)
        {
            // Test compositions without the membership repo wired up
            // degrade to "direct-read only" — same as today.
            return result;
        }

        if (!Guid.TryParse(actorId, out var agentUuid))
        {
            return result;
        }

        var parentRows = await memberships
            .ListByAgentAsync(agentUuid, cancellationToken)
            .ConfigureAwait(false);
        if (parentRows.Count == 0)
        {
            return result;
        }

        var orderedParents = await OrderParentsByDisplayNameAsync(
            sp,
            parentRows,
            cancellationToken).ConfigureAwait(false);

        foreach (var parentUnitId in orderedParents)
        {
            var parentKey = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(parentUnitId);
            var parentBundles = await unitStore.GetAsync(parentKey, cancellationToken).ConfigureAwait(false);
            if (parentBundles is null || parentBundles.Count == 0)
            {
                continue;
            }
            foreach (var bundle in parentBundles)
            {
                AppendIfNew(bundle, result, seen);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the parent unit ids in display-name order. Falls back to
    /// the membership table's natural <c>CreatedAt</c> order when the
    /// <see cref="SpringDbContext"/> isn't available in the scope (test
    /// compositions without the data layer wired). Untranslatable rows
    /// (no matching unit-definition row) sort after the known ones,
    /// stable on UUID order, so the function is total.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> OrderParentsByDisplayNameAsync(
        IServiceProvider sp,
        IReadOnlyList<UnitMembership> parents,
        CancellationToken cancellationToken)
    {
        var distinctIds = parents.Select(p => p.UnitId).Distinct().ToList();
        if (distinctIds.Count <= 1)
        {
            return distinctIds;
        }

        var db = sp.GetService<SpringDbContext>();
        if (db is null)
        {
            // Test composition without EF wired — keep the natural
            // CreatedAt order from the membership repo.
            return distinctIds;
        }

        var names = await db.UnitDefinitions
            .AsNoTracking()
            .Where(u => distinctIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var nameById = names.ToDictionary(n => n.Id, n => n.DisplayName ?? string.Empty);

        return distinctIds
            .OrderBy(id => nameById.TryGetValue(id, out var name) ? 0 : 1)
            .ThenBy(
                id => nameById.TryGetValue(id, out var name) ? name : string.Empty,
                StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true))
            .ThenBy(id => id)
            .ToList();
    }

    private static void AppendIfNew(
        SkillBundle bundle,
        List<SkillBundle> result,
        HashSet<(string Package, string Skill)> seen)
    {
        var key = (bundle.PackageName, bundle.SkillName);
        if (seen.Add(key))
        {
            result.Add(bundle);
        }
    }
}
