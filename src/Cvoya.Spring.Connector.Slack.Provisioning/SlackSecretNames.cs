// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Provisioning;

/// <summary>
/// Canonical tenant- / platform-secret names for the Slack OAuth app
/// credentials. This is the single source of truth shared across every
/// install surface (#2882):
/// <list type="bullet">
///   <item><description>the <c>spring connector slack install --write-{secrets,tenant-secrets}</c> CLI verb (via <c>SlackCredentialWriter</c>),</description></item>
///   <item><description>the server-side <c>POST /api/v1/tenant/connectors/slack/install</c> endpoint, and</description></item>
///   <item><description>the runtime <c>SlackOAuthOptionsResolver</c>, which resolves the four credential fields it consumes (client id, client secret, signing secret, redirect uri) through the tenant → platform → env precedence chain (issue #2849).</description></item>
/// </list>
/// Because all writers and the reader reference these same constants, the
/// portal wizard and the CLI persist byte-identical secret names — the
/// parity guarantee #2882's acceptance criteria require.
/// </summary>
public static class SlackSecretNames
{
    /// <summary>Slack app id. Diagnostic — not consumed by the OAuth resolver.</summary>
    public const string AppId = "slack-app-id";

    /// <summary>Slack OAuth client id. Public — surfaces in the authorize URL.</summary>
    public const string ClientId = "slack-oauth-client-id";

    /// <summary>Slack OAuth client secret. Server-side only — used in the <c>oauth.v2.access</c> exchange.</summary>
    public const string ClientSecret = "slack-oauth-client-secret";

    /// <summary>Slack app signing secret. Validates the <c>X-Slack-Signature</c> header on inbound webhooks.</summary>
    public const string SigningSecret = "slack-oauth-signing-secret";

    /// <summary>Slack verification token. Diagnostic — not consumed by the OAuth resolver.</summary>
    public const string VerificationToken = "slack-oauth-verification-token";

    /// <summary>Configured OAuth redirect URI. Must match the value baked into the app manifest.</summary>
    public const string RedirectUri = "slack-oauth-redirect-uri";
}
