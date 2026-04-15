// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.RateLimit;

using System.Net.Http.Headers;

/// <summary>
/// Tracks per-resource GitHub rate-limit quotas and lets callers wait
/// (preflight) before consuming the last slice of the window.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe: the tracker is a singleton and is
/// called concurrently from every in-flight HTTP request. Persistence
/// across restart / replicas is delegated to
/// <see cref="IRateLimitStateStore"/>; the tracker always maintains its
/// own in-memory cache for the hot path and uses the store to durably
/// echo state so it survives restarts and (optionally) converges across
/// replicas.
/// </remarks>
public interface IGitHubRateLimitTracker
{
    /// <summary>
    /// Returns the most recently observed quota for the given resource, or
    /// <c>null</c> if no response has ever updated it.
    /// </summary>
    RateLimitQuota? GetQuota(string resource);

    /// <summary>
    /// Merges <c>x-ratelimit-*</c> response headers into the tracker's view of
    /// the resource quota. Called after every successful or unsuccessful HTTP
    /// response. Safe to call from any thread.
    /// </summary>
    /// <param name="responseHeaders">Headers from the raw HTTP response.</param>
    void UpdateFromHeaders(HttpResponseHeaders responseHeaders);

    /// <summary>
    /// If the tracked <paramref name="resource"/> is below the configured safety
    /// threshold and its reset is in the future, waits until reset. Returns
    /// immediately when quota is healthy or the resource has never been observed.
    /// </summary>
    /// <param name="resource">Rate-limit resource (e.g. <c>core</c>, <c>search</c>,
    /// <c>graphql</c>).</param>
    /// <param name="cancellationToken">Token to cancel the wait.</param>
    Task WaitIfNeededAsync(string resource, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds the tracker's in-memory view from
    /// <see cref="IRateLimitStateStore.ReadAllAsync(string, CancellationToken)"/>.
    /// Called once on startup so the first caller after a restart has a
    /// preflight signal rather than waiting for the next real response
    /// to observe a quota. Safe to call multiple times; the tracker
    /// treats locally-observed snapshots as newer when their
    /// <c>ObservedAt</c> is beyond the persisted <c>UpdatedAt</c>.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the bulk read.</param>
    Task SeedFromStateStoreAsync(CancellationToken cancellationToken = default);
}