// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.WebApi;

using System.Text.Json;

/// <summary>
/// HTTP wrapper for the Slack Web API endpoints the runtime loop calls
/// — chat / conversations / views / users. Lives behind this interface
/// so tests can substitute a fake (no Slack network) and so the cloud
/// overlay can layer rate-limit tracking, retry, or auditing on top
/// without re-implementing the wire format.
///
/// <para>
/// Distinct from <see cref="Slack.Auth.OAuth.ISlackOAuthHttpClient"/>:
/// that surface covers install / disconnect; this surface covers the
/// runtime loop's outbound / inbound / slash-command paths.
/// </para>
/// </summary>
public interface ISlackWebApiClient
{
    /// <summary>
    /// Opens the bot's DM with <paramref name="slackUserId"/> via
    /// <c>POST conversations.open</c>. Idempotent — Slack returns the
    /// existing IM channel on repeat calls.
    /// </summary>
    Task<SlackOpenConversationResult> OpenConversationAsync(
        string botToken,
        string slackUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Posts a message via <c>POST chat.postMessage</c>. When
    /// <paramref name="threadTs"/> is non-null the message lands as a
    /// reply on the named Slack thread; otherwise it lands as a top-
    /// level message in the channel.
    /// </summary>
    /// <param name="botToken">Bot OAuth access token.</param>
    /// <param name="channel">Target Slack channel id (DM or channel).</param>
    /// <param name="text">Message text (may include Slack mrkdwn).</param>
    /// <param name="threadTs">
    /// Optional parent <c>thread_ts</c>. Non-null = reply on that
    /// thread; null = post a new top-level message.
    /// </param>
    /// <param name="username">
    /// Optional persona-override display name (requires
    /// <c>chat:write.customize</c> scope).
    /// </param>
    /// <param name="iconUrl">Optional persona-override avatar URL.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task<SlackPostMessageResult> PostMessageAsync(
        string botToken,
        string channel,
        string text,
        string? threadTs,
        string? username,
        string? iconUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Leaves a channel via <c>POST conversations.leave</c>. Used by
    /// the auto-leave path (ADR-0061 §2.2).
    /// </summary>
    Task<SlackResult> ConversationsLeaveAsync(
        string botToken,
        string channelId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a Block Kit modal via <c>POST views.open</c>. Slack
    /// enforces a 3-second handshake budget on slash-command responses
    /// that open modals; callers must keep handler work tight.
    /// </summary>
    /// <param name="botToken">Bot OAuth access token.</param>
    /// <param name="triggerId">
    /// The <c>trigger_id</c> from the slash-command payload — Slack
    /// invalidates these in seconds.
    /// </param>
    /// <param name="viewPayload">
    /// The Block Kit view payload serialised as <see cref="JsonElement"/>.
    /// </param>
    Task<SlackResult> ViewsOpenAsync(
        string botToken,
        string triggerId,
        JsonElement viewPayload,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the bot's permalink for a given message via
    /// <c>GET chat.getPermalink</c>. Used by <c>/sv-threads</c> to
    /// produce deep-links into each Slack thread.
    /// </summary>
    Task<SlackPermalinkResult> GetPermalinkAsync(
        string botToken,
        string channelId,
        string messageTs,
        CancellationToken cancellationToken);
}

/// <summary>
/// Base envelope shared by every Slack Web API response — Slack's wire
/// format always carries an <c>ok</c> flag and an optional <c>error</c>
/// string. Callers branch on <see cref="Ok"/>; transport-level failures
/// surface as exceptions in the underlying <see cref="HttpClient"/>.
/// </summary>
/// <param name="Ok">Slack's top-level success flag.</param>
/// <param name="Error">Slack-supplied error code (<c>null</c> when <c>Ok</c>).</param>
public record SlackResult(bool Ok, string? Error);

/// <summary>Result of <c>conversations.open</c>.</summary>
public sealed record SlackOpenConversationResult(
    bool Ok,
    string? Error,
    string ChannelId) : SlackResult(Ok, Error);

/// <summary>Result of <c>chat.postMessage</c>.</summary>
public sealed record SlackPostMessageResult(
    bool Ok,
    string? Error,
    string ChannelId,
    string MessageTs) : SlackResult(Ok, Error);

/// <summary>Result of <c>chat.getPermalink</c>.</summary>
public sealed record SlackPermalinkResult(
    bool Ok,
    string? Error,
    string Permalink) : SlackResult(Ok, Error);
