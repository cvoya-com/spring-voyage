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
/// called concurrently from every in-flight HTTP request. Implementations
/// for this first installment hold state in process memory only — persistence
/// across restart / replicas is tracked as a separate follow-up.
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
}