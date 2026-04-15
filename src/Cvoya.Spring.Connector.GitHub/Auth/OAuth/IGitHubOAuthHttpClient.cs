// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Low-level transport over GitHub's OAuth HTTP surface — the
/// <c>/login/oauth/access_token</c> exchange and the
/// <c>/applications/{client_id}/grant</c> revocation. Split out as an
/// abstraction so tests can substitute a fake without spinning an
/// <see cref="HttpClient"/> and without mocking <see cref="HttpMessageHandler"/>.
///
/// <para>
/// This interface is intentionally narrow: no Octokit coupling, no
/// session-store knowledge. The orchestration service composes it with
/// <see cref="IOAuthStateStore"/> and <see cref="IOAuthSessionStore"/>.
/// </para>
/// </summary>
public interface IGitHubOAuthHttpClient
{
    /// <summary>
    /// Exchanges an authorization code for an access token by posting to
    /// GitHub's <c>https://github.com/login/oauth/access_token</c>
    /// endpoint. Always uses HTTPS at the transport level — see the
    /// default implementation for the enforcement.
    /// </summary>
    Task<OAuthTokenExchangeResult> ExchangeCodeAsync(
        string clientId,
        string clientSecret,
        string code,
        string redirectUri,
        CancellationToken ct);

    /// <summary>
    /// Revokes an OAuth user-to-server token via GitHub's
    /// <c>DELETE /applications/{client_id}/grant</c> endpoint. Returns
    /// <c>true</c> when the revocation call was accepted (204) or the token
    /// was already unknown (404) — both outcomes leave the token unusable
    /// so they are treated equivalently. Returns <c>false</c> when GitHub
    /// returned an unexpected status; the caller still deletes the local
    /// record but surfaces the failure to the operator.
    /// </summary>
    Task<bool> RevokeTokenAsync(
        string clientId,
        string clientSecret,
        string accessToken,
        CancellationToken ct);
}