// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using Cvoya.Spring.Core.Secrets;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Default <see cref="IGitHubOAuthClientFactory"/>. Resolves the access
/// token through <see cref="IOAuthSessionStore"/> + <see cref="ISecretStore"/>
/// lazily per call — no in-memory token cache, because OAuth tokens are
/// per-user and cache correctness is harder to reason about than the
/// sub-millisecond read cost.
/// </summary>
public class GitHubOAuthClientFactory : IGitHubOAuthClientFactory
{
    private static readonly ProductHeaderValue UserAgent = new("SpringVoyage-GitHubConnector");

    private readonly IOAuthSessionStore _sessionStore;
    private readonly ISecretStore _secretStore;
    private readonly ILogger _logger;

    /// <summary>Creates a new factory.</summary>
    public GitHubOAuthClientFactory(
        IOAuthSessionStore sessionStore,
        ISecretStore secretStore,
        ILoggerFactory loggerFactory)
    {
        _sessionStore = sessionStore;
        _secretStore = secretStore;
        _logger = loggerFactory.CreateLogger<GitHubOAuthClientFactory>();
    }

    /// <inheritdoc />
    public async Task<IGitHubClient> CreateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessionStore.GetAsync(sessionId, cancellationToken);
        if (session is null)
        {
            _logger.LogInformation("OAuth session {SessionId} not found", sessionId);
            throw new GitHubOAuthSessionNotFoundException(sessionId);
        }

        var token = await _secretStore.ReadAsync(session.AccessTokenStoreKey, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning(
                "OAuth session {SessionId} resolved but its access-token store entry is missing; treating as revoked",
                sessionId);
            throw new GitHubOAuthSessionNotFoundException(sessionId);
        }

        _logger.LogDebug(
            "Created OAuth-authenticated GitHub client for session {SessionId} (login={Login})",
            sessionId, session.Login);

        return new GitHubClient(UserAgent)
        {
            Credentials = new Credentials(token),
        };
    }
}