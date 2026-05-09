// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default in-memory <see cref="IInstallationTokenCache"/>. Coalesces
/// concurrent mint requests for the same installation onto a single in-flight
/// task via a per-installation lock, and treats any token within the
/// configured <see cref="InstallationTokenCacheOptions.ProactiveRefreshWindow"/>
/// as "about to expire" so the cliff is never reached under load.
/// </summary>
/// <remarks>
/// Multi-host coordination is out of scope for the default impl (see issue
/// tracker for the Redis-backed variant). Each host mints independently; the
/// goal is to avoid the per-host mint storm, not global single-flight.
/// </remarks>
public class InstallationTokenCache : IInstallationTokenCache
{
    private readonly InstallationTokenCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    // SemaphoreSlim per-installation acts as the mint lock. Using a semaphore
    // (rather than lock / Monitor) means the wait is async and cancellable —
    // callers don't block a thread pool thread on a slow GitHub round-trip.
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<long, InstallationAccessToken> _tokens = new();

    /// <summary>
    /// Initializes the cache with tuning options, a <see cref="TimeProvider"/>
    /// (allows deterministic tests), and a logger factory.
    /// </summary>
    public InstallationTokenCache(
        InstallationTokenCacheOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<InstallationTokenCache>();
    }

    /// <inheritdoc />
    public async Task<InstallationAccessToken> GetOrMintAsync(
        long installationId,
        Func<long, CancellationToken, Task<InstallationAccessToken>> mintAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mintAsync);

        // Fast path — token is present and still well inside its TTL.
        if (TryGetFresh(installationId, out var cached))
        {
            return cached;
        }

        var gate = _locks.GetOrAdd(installationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after taking the lock: the thread that raced us may
            // have already minted a token we can now reuse.
            if (TryGetFresh(installationId, out cached))
            {
                return cached;
            }

            var isRefresh = _tokens.ContainsKey(installationId);
            _logger.LogInformation(
                isRefresh
                    ? "Refreshing installation token for {InstallationId}"
                    : "Minting installation token for {InstallationId}",
                installationId);

            InstallationAccessToken minted;
            try
            {
                minted = await mintAsync(installationId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to mint installation token for {InstallationId}",
                    installationId);
                throw;
            }

            // Cap the effective expiry at CeilingTtl if configured — purely
            // defensive. We store the *capped* value so TryGetFresh treats it
            // uniformly.
            var effective = ApplyCeiling(minted);
            _tokens[installationId] = effective;
            return effective;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate(long installationId)
    {
        if (_tokens.TryRemove(installationId, out _))
        {
            _logger.LogInformation(
                "Invalidated cached installation token for {InstallationId}",
                installationId);
        }
    }

    private bool TryGetFresh(long installationId, out InstallationAccessToken token)
    {
        if (_tokens.TryGetValue(installationId, out var candidate))
        {
            var now = _timeProvider.GetUtcNow();
            if (candidate.ExpiresAt - now > _options.ProactiveRefreshWindow)
            {
                token = candidate;
                return true;
            }
        }

        token = default;
        return false;
    }

    private InstallationAccessToken ApplyCeiling(InstallationAccessToken minted)
    {
        if (_options.CeilingTtl <= TimeSpan.Zero)
        {
            return minted;
        }

        var now = _timeProvider.GetUtcNow();
        var ceiling = now + _options.CeilingTtl;
        return minted.ExpiresAt > ceiling
            ? new InstallationAccessToken(minted.Token, ceiling)
            : minted;
    }
}
