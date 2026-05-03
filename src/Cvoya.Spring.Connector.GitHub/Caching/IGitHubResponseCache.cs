// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Caching;

/// <summary>
/// Response cache for read-heavy GitHub API calls. Entries are typed at the
/// call site; the cache is opaque to the actual payload shape. Implementations
/// must be thread-safe — the GitHub connector invokes reads concurrently from
/// multiple skill dispatches.
/// </summary>
/// <remarks>
/// Webhook-driven invalidation uses <see cref="InvalidateByTagAsync"/> so a
/// single event (e.g. <c>pull_request.edited</c> on <c>owner/repo#42</c>) can
/// flush every cached read tagged <c>pr:owner/repo#42</c> in one call. The
/// OSS default is in-memory and per-host; see #275 for distributed variants.
/// </remarks>
public interface IGitHubResponseCache
{
    /// <summary>
    /// Returns the cached entry for <paramref name="key"/> when present and
    /// unexpired; <c>null</c> otherwise. The returned <see cref="CacheEntry{T}.Age"/>
    /// is the elapsed time since the entry was written.
    /// </summary>
    /// <typeparam name="T">The cached value type the caller wrote.</typeparam>
    /// <param name="key">The canonical cache key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<CacheEntry<T>?> TryGetAsync<T>(CacheKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/> with the
    /// given <paramref name="ttl"/>. The entry is additionally registered under
    /// every tag in <see cref="CacheKey.Tags"/> so
    /// <see cref="InvalidateByTagAsync"/> can flush it in bulk.
    /// </summary>
    /// <typeparam name="T">The cached value type.</typeparam>
    /// <param name="key">The canonical cache key.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="ttl">Time-to-live for the entry. Non-positive values mean "do not cache".</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetAsync<T>(CacheKey key, T value, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the entry for <paramref name="key"/> if present.
    /// </summary>
    Task InvalidateAsync(CacheKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every entry that was registered under <paramref name="tag"/>
    /// when it was written. Unknown tags are a silent no-op.
    /// </summary>
    Task InvalidateByTagAsync(string tag, CancellationToken cancellationToken = default);
}