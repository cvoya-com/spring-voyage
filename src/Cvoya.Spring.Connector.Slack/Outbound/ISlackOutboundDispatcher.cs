// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Outbound;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Surfaces an SV outbound <see cref="Message"/> on the Slack side
/// when the thread has a Slack-bound participant per ADR-0061 §3.
/// The dispatcher routes via <see cref="Routing.ISlackContainerRouter"/>,
/// looks up the SV-thread ↔ Slack-thread mapping
/// (<see cref="ISlackThreadMapStore"/>), and posts via
/// <see cref="WebApi.ISlackWebApiClient"/>.
///
/// <para>
/// First-message-on-thread → resolve the bound user, build the slug
/// (<see cref="Slug.ISlackThreadSlugBuilder"/>), post the parent
/// message via <c>chat.postMessage</c> with
/// <c>channel = openConversation(user)</c>, persist the
/// <c>thread_ts</c>.
/// </para>
/// <para>
/// Subsequent messages on the SV thread → <c>chat.postMessage</c>
/// with <c>thread_ts</c> set + persona override
/// (<see cref="ISlackPersonaBuilder"/>) for non-bound participants.
/// </para>
/// </summary>
public interface ISlackOutboundDispatcher
{
    /// <summary>
    /// Tries to surface <paramref name="message"/> in Slack. Returns
    /// <c>true</c> when the message was delivered (post succeeded);
    /// <c>false</c> when the thread has no Slack-mapped human or the
    /// outbound surface is not configured. Throws
    /// <see cref="NotSupportedException"/> on the
    /// <see cref="Routing.SlackContainerRoute.PrivateChannel"/> branch
    /// (reserved for the hybrid mode, ADR-0061 §7.2).
    /// </summary>
    /// <param name="message">The outbound SV message.</param>
    /// <param name="participants">
    /// The thread's participants. The caller is responsible for
    /// resolving any <c>human://</c> participants to their bound
    /// <c>TenantUserId</c> via
    /// <see cref="ITenantUserHumanResolver"/> so the routing function
    /// matches the bound-user list.
    /// </param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task<SlackOutboundResult> DispatchAsync(
        Message message,
        IReadOnlyList<Address> participants,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of an outbound Slack dispatch.
/// </summary>
public enum SlackOutboundResult
{
    /// <summary>The message was posted as a parent or reply in Slack.</summary>
    Delivered,

    /// <summary>
    /// The thread has no Slack-bound participant — there is nothing
    /// to deliver. Common in OSS when an A2A-only thread has no
    /// humans.
    /// </summary>
    NoSlackSurface,

    /// <summary>
    /// The tenant has no Slack binding configured — the connector is
    /// not installed or has been disconnected.
    /// </summary>
    NotBound,
}
