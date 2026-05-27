// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Inbound;

using System.Text.Json;

/// <summary>
/// Processes a verified Slack event payload (the
/// <c>event_callback</c> envelope) against the tenant binding. The
/// endpoint handler reads the raw body, runs signature verification,
/// then hands the parsed envelope to this surface.
///
/// <para>
/// Verbs handled in v0.1:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>member_joined_channel</c> where <c>user == authedBotUserId</c>
///     — auto-leave path per ADR-0061 §2.2 (gated on
///     <c>single_user_mode</c> per §7.3).
///   </description></item>
///   <item><description>
///     <c>message.im</c> from the bound user — construct an ADR-0060
///     envelope keyed on the SV thread the
///     <see cref="Outbound.ISlackThreadMapStore"/> resolves from the
///     <c>thread_ts</c>.
///   </description></item>
///   <item><description>
///     <c>message.im</c> from an unbound user — once-per-session
///     refusal per ADR-0061 §2.4.
///   </description></item>
/// </list>
/// </summary>
public interface ISlackEventDispatcher
{
    /// <summary>
    /// Dispatches a parsed Slack event envelope.
    /// </summary>
    /// <param name="eventEnvelope">
    /// The full <c>event_callback</c> payload as Slack delivered it.
    /// Carries <c>team_id</c>, <c>event</c> (the inner event), and
    /// the <c>authed_users</c> / <c>authorizations</c> array.
    /// </param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task<SlackEventDispatchOutcome> DispatchAsync(
        JsonElement eventEnvelope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of an inbound event dispatch.
/// </summary>
public enum SlackEventDispatchOutcome
{
    /// <summary>The event was processed (forwarded, refused, or auto-left).</summary>
    Handled,

    /// <summary>The event type was not recognised; nothing to do.</summary>
    Ignored,

    /// <summary>
    /// The event's <c>team_id</c> did not match a known tenant
    /// binding. Treated as a misconfiguration; the endpoint still
    /// returns 200 so Slack does not retry.
    /// </summary>
    UnknownTeam,
}
