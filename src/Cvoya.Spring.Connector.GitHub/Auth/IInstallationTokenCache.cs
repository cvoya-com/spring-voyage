// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

/// <summary>
/// Caches GitHub App installation access tokens keyed by installation id.
/// Extracted as an abstraction so the private (cloud) repo can substitute a
/// distributed (e.g. Redis-backed) implementation when coordinating across
/// multiple hosts — the default single-host impl is in-memory.
/// </summary>
public interface IInstallationTokenCache
{
    /// <summary>
    /// Returns a valid installation access token for the given installation,
    /// minting (and caching) a new one via <paramref name="mintAsync"/> only
    /// when the cache is empty or the cached token is within the proactive
    /// refresh window.
    /// </summary>
    /// <remarks>
    /// Concurrent requests for the same installation must coalesce onto a
    /// single in-flight mint call — the cache serialises minting per
    /// installation so callers never race to spend quota twice.
    /// </remarks>
    /// <param name="installationId">The GitHub App installation id.</param>
    /// <param name="mintAsync">Delegate that fetches a fresh token from GitHub when the cache misses.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A token that is valid at the time of return.</returns>
    Task<InstallationAccessToken> GetOrMintAsync(
        long installationId,
        Func<long, CancellationToken, Task<InstallationAccessToken>> mintAsync,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the cached token for the given installation, if any. Intended
    /// for callers that detect an upstream 401 (e.g. admin revoked the App)
    /// and want the next call to re-mint.
    /// </summary>
    /// <param name="installationId">The GitHub App installation id.</param>
    void Invalidate(long installationId);
}
