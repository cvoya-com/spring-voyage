// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Configuration for the OAuth App authentication surface. Bound from the
/// <c>GitHub:OAuth</c> configuration section alongside the existing GitHub App
/// options on <see cref="GitHubConnectorOptions"/>.
///
/// <para>
/// The GitHub OAuth App is a distinct credential from the GitHub App — it is
/// used to act as an end user who has interactively authorized the platform,
/// rather than as an installation bot. The two sets of credentials live
/// side-by-side so existing App-auth flows keep working unchanged; see the
/// PR that introduced this type for the motivation (issue #233).
/// </para>
///
/// <para>
/// <b>Secrets.</b> <see cref="ClientId"/> is a public identifier and safe to
/// bind directly from configuration. <see cref="ClientSecret"/> is a secret
/// and should be sourced from <c>ISecretResolver</c> at the DI boundary; the
/// binder accepts a plaintext value to keep local dev simple, but production
/// hosts are expected to wrap the resolver and write the resolved value into
/// these options via <c>PostConfigure</c>.
/// </para>
/// </summary>
public class GitHubOAuthOptions
{
    /// <summary>
    /// The OAuth App client id (public identifier). Empty when the OAuth
    /// surface is not configured — authorize endpoints return 502 in that
    /// case rather than silently minting broken URLs.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// The OAuth App client secret. Must be resolved via
    /// <c>ISecretResolver</c> in production and written into the options
    /// instance before the service provider hands them to the OAuth
    /// endpoints. Never logged.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// The redirect URI GitHub should bounce the user back to after the
    /// authorize step. Must be registered with the OAuth App on GitHub —
    /// mismatched URIs fail closed at the token-exchange step.
    /// </summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// Default OAuth scopes to request when the authorize endpoint body
    /// does not override them. Expressed as a list so callers can compose
    /// it in configuration; the connector joins with a single space when
    /// building the GitHub URL, matching GitHub's wire format.
    ///
    /// <para>
    /// Empty default — a v1-Parity flow asks callers to be explicit so
    /// minimal scopes are the default rather than a catch-all like
    /// <c>repo</c>.
    /// </para>
    /// </summary>
    public IList<string> Scopes { get; set; } = new List<string>();

    /// <summary>
    /// How long a pending-authorization <c>state</c> parameter is valid
    /// before the callback must arrive. Defaults to 10 minutes — long
    /// enough for a real user to authorize through GitHub's consent
    /// screens without leaving a stale state lying around.
    /// </summary>
    public TimeSpan StateTtl { get; set; } = TimeSpan.FromMinutes(10);
}