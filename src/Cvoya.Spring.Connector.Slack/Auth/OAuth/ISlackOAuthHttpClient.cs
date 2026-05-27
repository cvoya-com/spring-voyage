// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Auth.OAuth;

/// <summary>
/// Low-level HTTP wrapper for the Slack OAuth + identity endpoints
/// the connector hits during install / disconnect. Lives behind an
/// interface so tests can substitute a fake (no Slack network) and
/// so the cloud overlay can add interception (logging, rate-limit
/// tracking) without re-implementing the wire format.
/// </summary>
public interface ISlackOAuthHttpClient
{
    /// <summary>
    /// Exchanges an authorization <paramref name="code"/> for a bot
    /// access token via <c>POST oauth.v2.access</c>.
    /// </summary>
    Task<SlackOAuthExchangeResult> ExchangeCodeAsync(
        string clientId,
        string clientSecret,
        string code,
        string redirectUri,
        CancellationToken cancellationToken);

    /// <summary>
    /// Calls <c>GET team.info</c> with the supplied bot token to
    /// resolve the workspace metadata — in particular the
    /// <c>enterprise.id</c> field for the Grid-detection path.
    /// </summary>
    Task<SlackTeamInfo> GetTeamInfoAsync(string botAccessToken, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes the supplied <paramref name="botAccessToken"/> via
    /// <c>POST auth.revoke</c>. Returns the raw Slack outcome — the
    /// caller decides whether a failure aborts the disconnect.
    /// </summary>
    Task<SlackRevokeResult> RevokeTokenAsync(string botAccessToken, CancellationToken cancellationToken);
}

/// <summary>
/// Decoded shape of a successful (or unsuccessful) <c>oauth.v2.access</c>
/// response. Fields populated only when <see cref="Ok"/> is <c>true</c>.
/// </summary>
/// <param name="Ok">Slack's top-level success flag.</param>
/// <param name="Error">Slack-supplied error code when <see cref="Ok"/> is <c>false</c>.</param>
/// <param name="TeamId">The workspace id (<c>team.id</c>).</param>
/// <param name="TeamName">The workspace name (<c>team.name</c>).</param>
/// <param name="BotUserId">The bot identity's <c>user_id</c>.</param>
/// <param name="BotAccessToken">The bot's OAuth access token (xoxb-...).</param>
/// <param name="AuthedUserId">The OAuth installer's <c>user_id</c>.</param>
/// <param name="EnterpriseId">
/// The <c>enterprise.id</c> when the workspace is part of an
/// Enterprise Grid, <c>null</c> otherwise (ADR-0061 §2.3).
/// </param>
public record SlackOAuthExchangeResult(
    bool Ok,
    string? Error,
    string TeamId,
    string? TeamName,
    string BotUserId,
    string BotAccessToken,
    string AuthedUserId,
    string? EnterpriseId);

/// <summary>
/// Decoded shape of a successful <c>team.info</c> response.
/// </summary>
/// <param name="TeamId">Workspace id (<c>team.id</c>).</param>
/// <param name="TeamName">Workspace display name (<c>team.name</c>).</param>
/// <param name="EnterpriseId">
/// Non-null when the workspace is in an Enterprise Grid. Used as a
/// belt-and-braces probe alongside <c>oauth.v2.access</c>'s own
/// <c>enterprise.id</c> field.
/// </param>
public record SlackTeamInfo(string TeamId, string? TeamName, string? EnterpriseId);

/// <summary>
/// Decoded shape of an <c>auth.revoke</c> response. Slack returns
/// <c>{ "ok": true|false, "revoked": true|false, "error": "..." }</c>.
/// </summary>
public record SlackRevokeResult(bool Ok, bool Revoked, string? Error);
