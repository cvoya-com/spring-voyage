// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Routing;

/// <summary>
/// Discriminated union describing how an SV thread maps to a Slack
/// container per <see href="https://github.com/cvoya-com/spring-voyage/blob/main/docs/decisions/0061-slack-connector-oss-shape.md">ADR-0061</see>
/// §7.2 / §7.8. The routing function is the single source of truth on
/// "where does this thread surface in Slack?" — outbound delivery
/// reads the result uniformly; no path hardcodes <c>container == DM</c>.
///
/// <para>
/// <b>Branches</b>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="DirectMessage"/> — exactly one bound human in the
///     thread. The Slack-side container is the bot's DM with that
///     user. This is the only branch that fires in OSS v0.1.
///   </description></item>
///   <item><description>
///     <see cref="PrivateChannel"/> — multiple bound humans in the
///     thread. ADR-0061 §7.2 reserves this branch for the future
///     hybrid mode; constructing the route is supported (so tests
///     pin the seam) but consuming it throws
///     <see cref="NotSupportedException"/> in v0.1.
///   </description></item>
///   <item><description>
///     <see cref="None"/> — no bound human participates in the
///     thread. The thread has no Slack-side surface in v0.1.
///   </description></item>
/// </list>
/// </summary>
public abstract record SlackContainerRoute
{
    /// <summary>
    /// Sealed hierarchy — only the three nested records implement
    /// the base.
    /// </summary>
    private SlackContainerRoute() { }

    /// <summary>
    /// Route to the bot's DM with the bound user. The only branch
    /// that fires in OSS v0.1 (ADR-0061 §2.2).
    /// </summary>
    /// <param name="SlackUserId">
    /// The bound Slack user's <c>user_id</c>. The outbound dispatch
    /// opens the DM via <c>conversations.open</c> at first-use and
    /// posts into the resulting channel.
    /// </param>
    public sealed record DirectMessage(string SlackUserId) : SlackContainerRoute;

    /// <summary>
    /// Route to a private channel hosting multiple bound humans
    /// (ADR-0061 §7.2). Reserved for the future hybrid-mode
    /// implementation; v0.1 consumers throw
    /// <see cref="NotSupportedException"/> on this branch.
    /// </summary>
    /// <param name="ChannelId">The Slack channel id (<c>C...</c>).</param>
    public sealed record PrivateChannel(string ChannelId) : SlackContainerRoute;

    /// <summary>
    /// No Slack-mapped human participates in the thread; the thread
    /// has no Slack-side surface.
    /// </summary>
    public sealed record None : SlackContainerRoute
    {
        /// <summary>Shared empty instance.</summary>
        public static None Instance { get; } = new();
    }
}
