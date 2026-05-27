// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Inbound;

/// <summary>
/// Tracks which unbound Slack users the bot has already refused this
/// session per ADR-0061 §2.4. The bot replies once with the refusal
/// message and ignores subsequent DMs from the same user.
///
/// <para>
/// Idempotency is keyed on <c>(team_id, slack_user_id)</c>. The
/// in-memory OSS default forgets the set on restart — acceptable for
/// OSS where the bot DM is rarely encountered by unbound users.
/// Cloud overlays can substitute a Redis-backed version via
/// <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton"/>.
/// </para>
/// </summary>
public interface IUnboundUserRefusalGate
{
    /// <summary>
    /// Marks <paramref name="slackUserId"/> as refused-this-session
    /// and returns whether the caller should send the refusal message
    /// now. The result is <c>true</c> exactly once per
    /// <c>(team_id, slack_user_id)</c> tuple; subsequent calls return
    /// <c>false</c>.
    /// </summary>
    bool TryClaimRefusal(string teamId, string slackUserId);
}
