// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Resolves the set of GitHub account logins a user "owns" — their personal
/// login plus every organisation they belong to. Used by the
/// <c>list-repositories</c> endpoint to filter App installations to only
/// those whose account matches the calling user's identity, preventing
/// cross-tenant repository leakage when the App is installed on multiple
/// organisations.
/// </summary>
public interface IGitHubUserScopeResolver
{
    /// <summary>
    /// Returns the set of GitHub login names the user represented by
    /// <paramref name="accessToken"/> has access to: their own login, plus
    /// the login of every organisation they belong to.
    /// </summary>
    /// <param name="accessToken">
    /// A valid GitHub OAuth user access token with at least <c>read:org</c>
    /// scope. The token is never returned to the caller; it is only used
    /// for the duration of this call.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A set of login names (case-insensitive comparison is the caller's
    /// responsibility — GitHub logins are case-preserving but
    /// case-insensitive).
    /// </returns>
    Task<IReadOnlySet<string>> ResolveAsync(
        string accessToken,
        CancellationToken cancellationToken = default);
}