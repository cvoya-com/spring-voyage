// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists the SV-thread ↔ Slack-thread mapping (ADR-0061 §3). One
/// row per <c>(tenant_id, sv_thread_id, bound_tenant_user_id, team_id)</c>:
/// when the bot first surfaces an SV thread inside a bound user's Slack
/// DM it posts a parent message whose <c>thread_ts</c> identifies the
/// resulting Slack thread; subsequent messages on the SV thread are
/// posted as threaded replies with that <c>thread_ts</c> set.
///
/// <para>
/// The compound key carries every dimension the lookup happens on:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Outbound: <c>(tenant_id, sv_thread_id, bound_tenant_user_id, team_id)</c>
///     decides whether this is the first message on the SV thread for
///     the bound user (no row → post parent + insert) or a reply
///     (row present → post into <c>thread_ts</c>).
///   </description></item>
///   <item><description>
///     Inbound: <c>(tenant_id, team_id, slack_thread_ts)</c> reverses
///     the lookup so a Slack reply event lands on the SV thread that
///     produced the parent. Backed by a unique index on
///     <c>(tenant_id, team_id, slack_thread_ts)</c>.
///   </description></item>
/// </list>
///
/// <para>
/// ADR-0061 §7.1: bound users are a list — the table key carries
/// <c>bound_tenant_user_id</c> so multi-user installs can hold multiple
/// rows per SV thread (one per bound user the thread surfaces to).
/// In OSS v0.1 the list has length 1; nothing assumes singleton.
/// </para>
/// </summary>
public class SlackThreadStateEntity : ITenantScopedEntity
{
    /// <summary>Synthetic primary key.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>
    /// The SV thread id this row maps. Identical to
    /// <c>ThreadEntity.Id</c>; no FK is declared so the table can be
    /// dropped or rebuilt without entangling thread-registry rows.
    /// </summary>
    public Guid SvThreadId { get; set; }

    /// <summary>
    /// The <see cref="Cvoya.Spring.Dapr.Data.Entities.TenantUserEntity"/>
    /// the Slack-side surface is rendered for (ADR-0061 §7.1). In OSS
    /// this is always <c>OssTenantUserIds.Operator</c>; cloud / multi-
    /// user installs carry one row per bound user that surfaces the
    /// thread.
    /// </summary>
    public Guid BoundTenantUserId { get; set; }

    /// <summary>
    /// Slack workspace id (<c>team.id</c>). Recorded so the inbound
    /// reverse-lookup (<c>(tenant, team_id, thread_ts)</c>) can resolve
    /// cleanly even if a single tenant ever rebinds to a different
    /// workspace within a release.
    /// </summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>
    /// The Slack <c>thread_ts</c> of the parent message inside the
    /// bound user's bot DM. The opaque string Slack hands back from
    /// <c>chat.postMessage</c>'s <c>ts</c> field. Stored as text since
    /// it's a Slack-side timestamp string, not a sortable numeric.
    /// </summary>
    public string SlackThreadTs { get; set; } = string.Empty;

    /// <summary>
    /// Slack DM channel id where the parent message was posted (Slack
    /// <c>chat.postMessage</c> returns <c>channel</c>). Persisted so
    /// subsequent replies can post into the same channel without a
    /// round-trip to <c>conversations.open</c>.
    /// </summary>
    public string SlackChannelId { get; set; } = string.Empty;

    /// <summary>UTC timestamp of when the row was first inserted.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
