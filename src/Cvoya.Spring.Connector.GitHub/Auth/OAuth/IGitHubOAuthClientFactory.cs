// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using Octokit;

/// <summary>
/// Creates an Octokit <see cref="IGitHubClient"/> authenticated as the user
/// behind an OAuth session. Separate from <see cref="GitHubConnector"/>'s
/// App-installation client factory so nothing accidentally mixes the two
/// credential types.
///
/// <para>
/// Looks up the access token through <see cref="IOAuthSessionStore"/> +
/// <see cref="Cvoya.Spring.Core.Secrets.ISecretStore"/> on every call — the
/// cost is one in-memory lookup plus one opaque secret-store read, both in
/// the sub-millisecond range. Memoising across calls is tempting but makes
/// revocation harder to reason about.
/// </para>
/// </summary>
public interface IGitHubOAuthClientFactory
{
    /// <summary>
    /// Returns an Octokit client authenticated as the user behind the
    /// given session id. Throws <see cref="GitHubOAuthSessionNotFoundException"/>
    /// when the session is unknown or the underlying token has been
    /// purged from the secret store.
    /// </summary>
    Task<IGitHubClient> CreateAsync(string sessionId, CancellationToken cancellationToken = default);
}