// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Configuration;

/// <summary>
/// Configuration bound from the <c>Slack:OAuth</c> section. The
/// operator registers a Slack app on api.slack.com and pastes the
/// resulting credentials into <c>spring.env</c>; the host validates
/// presence at startup.
/// </summary>
public class SlackOAuthOptions
{
    /// <summary>
    /// Slack app <c>Client ID</c>. Public — surfaces in the
    /// authorize URL.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Slack app <c>Client Secret</c>. Server-side only — required
    /// at callback time for the <c>oauth.v2.access</c> exchange.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Slack app <c>Signing Secret</c>. Required at callback time so
    /// it can be persisted as a tenant secret alongside the bot
    /// token; the inbound event handler (#2817) reads it on every
    /// webhook to validate Slack's <c>X-Slack-Signature</c> header.
    /// </summary>
    public string SigningSecret { get; set; } = string.Empty;

    /// <summary>
    /// Configured OAuth redirect URI. Must match the value
    /// registered on api.slack.com for the connector's Slack app.
    /// </summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// Space-joined Slack scope list. Default matches ADR-0061 §6 —
    /// the minimum scopes for DM-only operation, persona overrides,
    /// slash commands, and the auto-leave / member-joined-channel
    /// path.
    /// </summary>
    public string Scopes { get; set; } =
        "chat:write chat:write.customize im:history im:write im:read users:read users:read.email commands channels:read groups:read";

    /// <summary>
    /// How long an in-flight authorize state is valid before the
    /// callback handler refuses to consume it. Defaults to 15 minutes.
    /// </summary>
    public TimeSpan StateTtl { get; set; } = TimeSpan.FromMinutes(15);
}
