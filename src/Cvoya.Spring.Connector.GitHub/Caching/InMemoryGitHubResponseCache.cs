// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Caching;

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IGitHubResponseCache"/> implementation. Each entry is
/// stored in a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by the
/// canonical <see cref="CacheKey"/>; tag membership is tracked in a second
/// dictionary (tag &#x2192; set of keys) so <see cref="InvalidateByTagAsync"/>
/// can flush every entry that registered under a tag in one pass.
/// </summary>
/// <remarks>
/// Per-host only. Multi-host coordination (shared storage + invalidation
/// bus) is tracked as a follow-up; the interface stays stable so the cloud
/// host can swap the implementation via DI without touching call sites.
/// </remarks>
public sealed class InMemoryGitHubResponseCache : IGitHubResponseCache, IDisposable
{
    private readonly GitHubResponseCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ITimer? _cleanupTimer;

    private readonly ConcurrentDictionary<CacheKey, Entry> _entries = new();

    // Tag &#x2192; set of keys registered under that tag. HashSet is guarded
    // by a lock on the set itself; a HashSet is fine here because the typical
    // fanout is single digits (pr: / issue: / repo:) per entry, so lock
    // contention is negligible compared to read / write amplification.
    private readonly ConcurrentDictionary<string, HashSet<CacheKey>> _tagIndex =
        new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>
    /// Initializes the cache with tuning options, a <see cref="TimeProvider"/>
    /// (allows deterministic tests), and a logger factory. Starts a background
    /// sweep timer unless <see cref="GitHubResponseCacheOptions.CleanupInterval"/>
    /// is non-positive.
    /// </summary>
    public InMemoryGitHubResponseCache(
        GitHubResponseCacheOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<InMemoryGitHubResponseCache>();

        if (_options.CleanupInterval > TimeSpan.Zero)
        {
            _cleanupTimer = _timeProvider.CreateTimer(
                _ => SweepExpired(),
                state: null,
                dueTime: _options.CleanupInterval,
                period: _options.CleanupInterval);
        }
    }

    /// <inheritdoc />
    public Task<CacheEntry<T>?> TryGetAsync<T>(CacheKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!_entries.TryGetValue(key, out var entry))
        {
            _logger.LogDebug("Cache MISS for {CacheKey}", key);
            return Task.FromResult<CacheEntry<T>?>(null);
        }

        var now = _timeProvider.GetUtcNow();
        if (entry.ExpiresAt <= now)
        {
            // Expired — remove opportunistically so the next caller doesn't
            // re-read the stale entry and avoid holding a reference to the
            // value longer than the TTL promised.
            RemoveEntry(key);
            _logger.LogDebug("Cache MISS (expired) for {CacheKey}", key);
            return Task.FromResult<CacheEntry<T>?>(null);
        }

        if (entry.Value is not T typed)
        {
            // Type mismatch — the caller changed the stored shape for this
            // key. Treat as a miss so the caller re-fetches and overwrites.
            _logger.LogWarning(
                "Cache MISS (type mismatch) for {CacheKey}: stored {Stored} requested {Requested}",
                key, entry.Value?.GetType().FullName ?? "null", typeof(T).FullName);
            return Task.FromResult<CacheEntry<T>?>(null);
        }

        var age = now - entry.CreatedAt;
        _logger.LogDebug("Cache HIT for {CacheKey} age={Age}", key, age);
        return Task.FromResult<CacheEntry<T>?>(new CacheEntry<T>(typed, age));
    }

    /// <inheritdoc />
    public Task SetAsync<T>(CacheKey key, T value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (ttl <= TimeSpan.Zero)
        {
            // Explicit "do not cache" signal — drop any prior value so reads
            // don't keep returning the previous response once the caller
            // decided it's no longer cacheable.
            InvalidateInternal(key);
            return Task.CompletedTask;
        }

        var now = _timeProvider.GetUtcNow();
        var entry = new Entry(value!, now, now + ttl, key.Tags);

        // Insert / replace the entry first so concurrent reads see either the
        // old or the new value, never an in-between state with stale tags.
        _entries[key] = entry;

        foreach (var tag in key.Tags)
        {
            var set = _tagIndex.GetOrAdd(tag, _ => new HashSet<CacheKey>());
            lock (set)
            {
                set.Add(key);
            }
        }

        _logger.LogDebug(
            "Cache SET for {CacheKey} ttl={Ttl} tags={TagCount}",
            key, ttl, key.Tags.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task InvalidateAsync(CacheKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (InvalidateInternal(key))
        {
            _logger.LogDebug("Cache INVALIDATE for {CacheKey}", key);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task InvalidateByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);

        if (!_tagIndex.TryRemove(tag, out var keys))
        {
            return Task.CompletedTask;
        }

        CacheKey[] snapshot;
        lock (keys)
        {
            snapshot = [.. keys];
        }

        foreach (var key in snapshot)
        {
            InvalidateInternal(key);
        }

        _logger.LogDebug(
            "Cache INVALIDATE_BY_TAG {Tag} removed={Count}",
            tag, snapshot.Length);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sweeps expired entries. Exposed as a public surface so diagnostic
    /// tools and tests can trigger a sweep deterministically; production
    /// callers rely on the background timer configured via
    /// <see cref="GitHubResponseCacheOptions.CleanupInterval"/>.
    /// </summary>
    public void SweepExpired()
    {
        if (_disposed)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var removed = 0;
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                if (InvalidateInternal(key))
                {
                    removed++;
                }
            }
        }

        if (removed > 0)
        {
            _logger.LogDebug("Cache sweep removed {Count} expired entries", removed);
        }
    }

    private bool InvalidateInternal(CacheKey key)
    {
        if (!_entries.TryRemove(key, out var entry))
        {
            return false;
        }

        // Drop the reverse-index membership so
        // <see cref="InvalidateByTagAsync"/> never tries to re-invalidate
        // an entry that no longer exists. Leaving empty sets in place is
        // harmless; skipping the removal keeps the hot path single-write.
        foreach (var tag in entry.Tags)
        {
            if (_tagIndex.TryGetValue(tag, out var set))
            {
                lock (set)
                {
                    set.Remove(key);
                }
            }
        }

        return true;
    }

    private void RemoveEntry(CacheKey key) => InvalidateInternal(key);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cleanupTimer?.Dispose();
    }

    private sealed record Entry(
        object Value,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        IReadOnlyList<string> Tags);
}