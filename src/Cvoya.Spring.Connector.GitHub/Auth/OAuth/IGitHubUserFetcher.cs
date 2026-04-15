// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Wraps the <c>GET /user</c> call so the OAuth service can resolve the
/// freshly-issued token to a login without hand-constructing an Octokit
/// client for a single request. Having it behind an interface keeps the
/// service easy to test — the default implementation uses Octokit, but
/// tests pass a stub.
/// </summary>
public interface IGitHubUserFetcher
{
    /// <summary>
    /// Calls <c>GET /user</c> with the given token and returns a subset
    /// of the response. Throws on any non-2xx so the caller can fail
    /// closed.
    /// </summary>
    Task<GitHubUserIdentity> GetAsync(string accessToken, CancellationToken ct);
}

/// <summary>
/// Minimal projection of <c>GET /user</c> the connector needs — login and
/// numeric id are enough to key a session; the skill returns the wider set.
/// </summary>
/// <param name="Login">The GitHub login.</param>
/// <param name="Id">The numeric user id.</param>
/// <param name="Name">The display name, if the user has one.</param>
/// <param name="Email">The public email, if the user has one.</param>
public record GitHubUserIdentity(string Login, long Id, string? Name, string? Email);