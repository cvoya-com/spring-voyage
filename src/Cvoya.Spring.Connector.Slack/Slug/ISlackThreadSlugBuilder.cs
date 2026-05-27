// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Slug;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Builds the human-readable parent-message slug that names the
/// SV-side participants on a Slack thread inside the bound user's
/// bot DM. Per
/// <see href="https://github.com/cvoya-com/spring-voyage/blob/main/docs/decisions/0061-slack-connector-oss-shape.md">ADR-0061 §4</see>:
///
/// <para>
/// <em>"Concatenate the display names of every SV participant in the
/// thread, separated by <c>-</c>, dropping the one SV human designated
/// as primary for the bound <c>TenantUser</c>, prefixed with
/// <c>sv-</c>."</em>
/// </para>
///
/// <para>
/// The "Hat-to-drop" (the one SV human removed from the slug) is the
/// Hat the SV thread is rendered as for the bound <c>TenantUser</c>.
/// <see href="https://github.com/cvoya-com/spring-voyage/blob/main/docs/decisions/0062-tenant-user-human-explicit-binding.md">ADR-0062 §5</see>
/// pins this per-thread: replies adopt the Hat the thread came in on;
/// new threads (started via <c>/sv-thread</c>) fall back to
/// <c>T.PrimaryHumanId</c>. The slug-builder consumes
/// <see cref="ITenantUserHumanResolver"/> for both branches — there is
/// no separate Slack-side resolver. ADR-0061 §7.4 forward-compat seam
/// preserved: this surface does not branch on deployment mode and
/// multi-tenant generalisations land entirely in the resolver, not
/// here.
/// </para>
///
/// <para>
/// <b>Worked examples</b> (ADR-0061 §4 — bound <c>TenantUser</c> is
/// the OSS operator; primary SV Human is "alex"):
/// </para>
/// <list type="table">
///   <listheader>
///     <term>SV thread participant set</term>
///     <description>Slack-thread parent slug</description>
///   </listheader>
///   <item>
///     <term><c>{human:alex, agent:bob}</c></term>
///     <description><c>sv-bob</c></description>
///   </item>
///   <item>
///     <term><c>{human:alex, agent:bob, unit:research}</c></term>
///     <description><c>sv-bob-research</c></description>
///   </item>
///   <item>
///     <term><c>{human:alex, agent:bob, unit:research, human:morgan}</c></term>
///     <description><c>sv-bob-research-morgan</c></description>
///   </item>
///   <item>
///     <term><c>{human:alex, human:morgan, agent:bob}</c></term>
///     <description><c>sv-bob-morgan</c></description>
///   </item>
/// </list>
///
/// <para>
/// <b>Determinism.</b> The same participant set always produces the
/// same slug. Participants are ordered by their canonical wire form
/// (<see cref="Address.ToString"/>) so the rule agrees with the
/// participant-set canonicalisation the platform's thread-identity
/// layer uses — two different code paths cannot disagree on "the
/// same thread."
/// </para>
/// </summary>
public interface ISlackThreadSlugBuilder
{
    /// <summary>
    /// Builds the Slack parent-message slug naming the SV-side
    /// participants on a thread, with the bound user's Hat removed
    /// per ADR-0061 §4.
    /// </summary>
    /// <param name="participants">
    /// Every participant of the SV thread (humans, agents, units).
    /// Order is irrelevant — the builder canonicalises before
    /// concatenating. Must contain at least one element.
    /// </param>
    /// <param name="boundTenantUserId">
    /// The <c>TenantUser</c> id of the Slack-bound user whose Hat is
    /// to be dropped from the slug. In OSS v0.1 this is always
    /// <c>OssTenantUserIds.Operator</c>; in cloud / multi-user
    /// installs this is the specific <c>TenantUser</c> the slug is
    /// rendered for.
    /// </param>
    /// <param name="threadId">
    /// The SV thread id (Guid). Forwarded to
    /// <see cref="ITenantUserHumanResolver.PickFromAsync"/> so the
    /// per-thread Hat pinning (ADR-0062 §5) selects the Hat the
    /// thread is rendered as. Pass <see cref="Guid.Empty"/> for
    /// new-thread paths with no thread row yet (the resolver falls
    /// back to <c>PrimaryHumanId</c>).
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    /// <returns>
    /// The slug as a <see cref="string"/> in the form
    /// <c>sv-&lt;name&gt;-&lt;name&gt;-…</c>. Always begins with
    /// <c>sv-</c>; the suffix is the dash-separated display names of
    /// the participants, each slugified to ASCII lowercase
    /// <c>[a-z0-9-]</c>. When dropping the Hat empties the set (the
    /// rare 1-participant case where the thread is solo with the
    /// bound human), returns the literal <c>sv</c> prefix only — no
    /// trailing dash.
    /// </returns>
    Task<string> BuildSlugAsync(
        IReadOnlyList<Address> participants,
        Guid boundTenantUserId,
        Guid threadId,
        CancellationToken cancellationToken = default);
}
