// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves which "Hats" (<c>Human</c> rows bound to a tenant user) may
/// address a given target, enforcing the v0.1 Hat ↔ unit reachability rule
/// (#2972).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule.</b> A Hat <c>H</c> is a <em>direct human member</em> of a
/// unit <c>U</c> (a <c>unit_memberships_humans</c> row). <c>H</c> reaches
/// exactly:
/// </para>
/// <list type="bullet">
///   <item><description><c>U</c> itself.</description></item>
///   <item><description>Every <em>direct</em> member of <c>U</c> — agent
///     members (<c>unit_memberships</c>), sub-unit members
///     (<c>unit_subunit_memberships</c>), and co-member humans
///     (<c>unit_memberships_humans</c>).</description></item>
/// </list>
/// <para>
/// It does <b>not</b> reach <c>U</c>'s parent (so
/// <c>UnitA → SubUnitB → HumanC</c> ⇒ HumanC cannot reach UnitA), nor
/// <em>into</em> a sibling sub-unit (so a Hat in UnitA reaches SubUnitB the
/// unit, but not SubUnitB's own members). "A direct human member under the
/// unit is required" is the <em>sender-side</em> requirement — you only get
/// a Hat by being a direct member of some unit; this service answers the
/// <em>target-side</em> question.
/// </para>
/// <para>
/// A tenant user may message a target <c>T</c> only when they wear at least
/// one Hat that reaches <c>T</c>; the message-send endpoints reject the send
/// otherwise (the platform gate). The portal from-selector and the CLI
/// <c>--as</c> resolution narrow the offered Hats to the wearable set so the
/// operator is never shown a Hat that cannot address the target.
/// </para>
/// <para>
/// <b>Why an interface in Core.</b> Pinning the contract in
/// <see cref="Cvoya.Spring.Core.Units"/> alongside
/// <see cref="IUnitHumanMembershipStore"/> lets the cloud overlay register a
/// tenant-aware decorator (or a configurable-policy variant — the v0.1
/// rule is fixed; see the follow-up tracked on #2972) without depending on
/// <c>Cvoya.Spring.Dapr</c>. The default OSS/cloud implementation lives in
/// <c>Cvoya.Spring.Dapr.Units</c> and walks the membership tables through
/// <c>SpringDbContext</c>; its tenant query filter scopes every read to the
/// active tenant.
/// </para>
/// </remarks>
public interface IHatReachabilityService
{
    /// <summary>
    /// Returns the ids of the tenant user's bound Hats that reach
    /// <b>every</b> target in <paramref name="targets"/> (the intersection,
    /// so a multi-recipient send needs one Hat that can address them all).
    /// </summary>
    /// <param name="tenantUserId">
    /// The authenticated caller's <c>TenantUser</c> id. Never
    /// <see cref="Guid.Empty"/>.
    /// </param>
    /// <param name="targets">
    /// The recipients the Hat must be able to address. Only
    /// <see cref="Address.UnitScheme"/>, <see cref="Address.AgentScheme"/>,
    /// and <see cref="Address.HumanScheme"/> targets participate in the
    /// rule; any other scheme reaches nothing and yields an empty result.
    /// An empty collection is treated as "no constraint" and returns the
    /// caller's full bound-Hat set (the unscoped listing case).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The wearable Hat ids, in no particular order. Empty when the caller
    /// has no bound Hats, or no bound Hat reaches all targets (the gate
    /// rejection signal).
    /// </returns>
    Task<IReadOnlyList<Guid>> GetWearableHatsAsync(
        Guid tenantUserId,
        IReadOnlyCollection<Address> targets,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <see langword="true"/> when the Hat
    /// <paramref name="humanId"/> reaches <paramref name="target"/> under
    /// the reachability rule. Used to validate an explicit "speaking-as"
    /// override that is already known to be bound to the caller.
    /// </summary>
    /// <param name="humanId">The Hat (Human row) id.</param>
    /// <param name="target">
    /// The recipient. Non-unit/agent/human schemes always return
    /// <see langword="false"/>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<bool> ReachesAsync(
        Guid humanId,
        Address target,
        CancellationToken cancellationToken = default);
}
