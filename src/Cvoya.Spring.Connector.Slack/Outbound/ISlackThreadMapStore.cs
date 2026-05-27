// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Outbound;

/// <summary>
/// SV-thread ↔ Slack-thread mapping store (ADR-0061 §3 / #2818).
/// Records the <c>thread_ts</c> of the parent Slack message the bot
/// posts for each SV thread per bound user, and reverses the lookup
/// so a Slack-side reply can route back to the SV thread.
///
/// <para>
/// Implementations are singleton; the EF context they wrap is
/// resolved per call via <see cref="IServiceScopeFactory"/> — the
/// same singleton-safety pattern <c>SlackInstallStore</c> uses.
/// </para>
/// </summary>
public interface ISlackThreadMapStore
{
    /// <summary>
    /// Persists a mapping. ADR-0061 §3: called once the first time
    /// the bot posts a parent message for an SV thread inside the
    /// bound user's DM.
    /// </summary>
    /// <param name="svThreadId">The SV thread id.</param>
    /// <param name="boundTenantUserId">
    /// The <c>TenantUser</c> the thread is rendered for. ADR-0061
    /// §7.1: multi-user installs have one mapping per bound user
    /// the thread surfaces to; OSS has length 1.
    /// </param>
    /// <param name="teamId">Slack workspace id.</param>
    /// <param name="slackChannelId">DM channel id from <c>conversations.open</c>.</param>
    /// <param name="slackThreadTs">
    /// The Slack <c>thread_ts</c> of the parent message
    /// (<c>chat.postMessage</c>'s <c>ts</c> field).
    /// </param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task RecordAsync(
        Guid svThreadId,
        Guid boundTenantUserId,
        string teamId,
        string slackChannelId,
        string slackThreadTs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the Slack mapping for the SV thread + bound user, or
    /// <c>null</c> when none exists yet. Used on the outbound path to
    /// decide whether to post a parent message or a threaded reply.
    /// </summary>
    Task<SlackThreadMapping?> LookupOutboundAsync(
        Guid svThreadId,
        Guid boundTenantUserId,
        string teamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inverse lookup: given a Slack reply's <c>thread_ts</c> + team,
    /// returns the SV thread id the reply belongs on. Used by the
    /// inbound events endpoint (#2817).
    /// </summary>
    /// <returns>
    /// The mapping, or <c>null</c> when no SV thread is associated
    /// with the supplied Slack thread.
    /// </returns>
    Task<SlackThreadMapping?> LookupSvThreadAsync(
        string teamId,
        string slackThreadTs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every SV thread mapping for the named bound user — used
    /// by <c>/sv-threads</c> (#2819).
    /// </summary>
    Task<IReadOnlyList<SlackThreadMapping>> ListForBoundUserAsync(
        Guid boundTenantUserId,
        string teamId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One mapping row returned by <see cref="ISlackThreadMapStore"/>.
/// </summary>
/// <param name="SvThreadId">SV thread id.</param>
/// <param name="BoundTenantUserId">The bound <c>TenantUser</c>.</param>
/// <param name="TeamId">Slack workspace id.</param>
/// <param name="SlackChannelId">Slack DM channel id.</param>
/// <param name="SlackThreadTs">The Slack <c>thread_ts</c> of the parent message.</param>
public sealed record SlackThreadMapping(
    Guid SvThreadId,
    Guid BoundTenantUserId,
    string TeamId,
    string SlackChannelId,
    string SlackThreadTs);
