// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Resolves the GitHub OAuth access token (and the associated login) for the
/// signed-in human behind the current request. Used by the connector's
/// repository-listing surface so the wizard's Repository dropdown is scoped
/// to the repos the signed-in GitHub user can actually access via the
/// configured GitHub App, not every repo the App can see across every other
/// user's installations (issue #1153).
/// </summary>
/// <remarks>
/// <para>
/// The OSS default implementation reads an <c>oauth_session_id</c> query
/// parameter (or, equivalently, an <c>X-GitHub-OAuth-Session</c> header) off
/// the ambient <see cref="Microsoft.AspNetCore.Http.HttpContext"/> and
/// resolves it through <see cref="IOAuthSessionStore"/> +
/// <see cref="Cvoya.Spring.Core.Secrets.ISecretStore"/>. Returning
/// <c>null</c> means "no signed-in GitHub user identity is available on this
/// request" — endpoints that need user scope MUST treat that as a
/// "sign-in required" condition rather than silently falling back to the
/// App-wide enumeration that produced the original bug.
/// </para>
/// <para>
/// Cloud overlays (private repo) substitute a tenant-aware implementation
/// that resolves the access token from the request principal — e.g. an
/// SSO session that carries a linked GitHub identity — without needing the
/// <c>oauth_session_id</c> query plumbing the OSS default uses.
/// </para>
/// </remarks>
public interface IGitHubUserAccessTokenProvider
{
    /// <summary>
    /// Returns the GitHub OAuth user access token for the current request,
    /// or <c>null</c> when no signed-in GitHub user identity is available.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<GitHubUserAccess?> GetCurrentAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The signed-in GitHub user's identity and the OAuth access token used to
/// scope GitHub API calls to that user. The token MUST never be persisted
/// or logged by callers — it lives only for the lifetime of the resolution.
/// </summary>
/// <param name="Login">The signed-in GitHub user's login.</param>
/// <param name="UserId">The signed-in GitHub user's numeric id.</param>
/// <param name="AccessToken">
/// The user-to-server OAuth access token. Use this to call user-scoped
/// GitHub endpoints such as <c>GET /user/installations</c>.
/// </param>
public record GitHubUserAccess(string Login, long UserId, string AccessToken);