// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Provisioning;

/// <summary>
/// The credentials a freshly-created Slack app yields, flattened from the
/// <c>apps.manifest.create</c> response plus the redirect URI the caller
/// derived from the SV host. Shared by the CLI's credential writer and the
/// server-side install endpoint so both persist the same fields under the
/// same <see cref="SlackSecretNames"/> (#2882).
/// </summary>
/// <param name="AppId">The Slack app id. May be omitted by Slack on draft creation.</param>
/// <param name="ClientId">The OAuth client id. Public; surfaces in the authorize URL.</param>
/// <param name="ClientSecret">The OAuth client secret. Required — the install fails earlier if Slack omits it.</param>
/// <param name="SigningSecret">The app signing secret. Required — validates inbound webhook signatures.</param>
/// <param name="VerificationToken">The legacy verification token. May be omitted by Slack.</param>
/// <param name="RedirectUri">The OAuth redirect URI baked into the manifest.</param>
public sealed record SlackProvisionedCredentials(
    string? AppId,
    string? ClientId,
    string ClientSecret,
    string SigningSecret,
    string? VerificationToken,
    string RedirectUri);
