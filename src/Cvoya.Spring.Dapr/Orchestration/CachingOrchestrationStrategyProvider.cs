// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Orchestration;

using Microsoft.Extensions.Logging;

/// <summary>
/// In-process caching decorator for <see cref="IOrchestrationStrategyProvider"/>
/// (#518). Wraps the inner provider (normally
/// <see cref="DbOrchestrationStrategyProvider"/>) so each domain message no
/// longer opens a fresh <c>AsyncServiceScope</c> and pays a Postgres
/// round-trip for a declarative slot that almost never changes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cache shape: hybrid short-TTL + explicit invalidation.</b> The cache
/// keeps each unit's resolved key for a bounded <c>Ttl</c> (30s by default)
/// and exposes <see cref="Invalidate"/> so known-write paths
/// (<c>UnitCreationService.PersistUnitDefinitionOrchestrationAsync</c>) can
/// drop the entry the instant they mutate the row. Within-process consumers
/// get immediate consistency after a write; cross-process writes (another
/// host replica mutating the same row) heal within the TTL window without
/// any cross-host coordination. The short TTL also protects against
/// invalidation paths we haven't identified yet — the cache is never
/// authoritative for longer than <c>Ttl</c>.
/// </para>
/// <para>
/// <b>Stampede protection.</b> Misses are coalesced through a
/// per-unit <see cref="SemaphoreSlim"/> so N concurrent readers that all miss
/// on the same unit issue exactly one inner read. The semaphore is released
/// once the miss resolves; the next miss (after TTL expiry or invalidation)
/// rebuilds it. Negative results (inner returned <c>null</c>) are cached
/// too — a unit with no <c>orchestration.strategy</c> block is the common
/// case and must not take a DB hit on every message.
/// </para>
/// <para>
/// <b>Failures are not cached.</b> If the inner provider throws, the
/// exception propagates to the caller and nothing is stored — the next
/// attempt retries. This mirrors the inner provider's own "degraded but
/// alive" contract: transient DB blips flow through as <c>null</c> at the
/// resolver boundary, not as a poisoned cache entry.
/// </para>
/// </remarks>
public class CachingOrchestrationStrategyProvider : IOrchestrationStrategyProvider, IOrchestrationStrategyCacheInvalidator
{
    /// <summary>
    /// Default time-to-live for a cached strategy key. Short enough that a
    /// cross-process write to the same unit heals quickly without an explicit
    /// invalidation signal; long enough to absorb a steady stream of domain
    /// messages to the same unit on a single ≤1 DB read budget.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    private readonly IOrchestrationStrategyProvider _inner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a caching decorator wrapping <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The inner provider to delegate to on cache miss.</param>
    /// <param name="timeProvider">Clock used to compute expiry. Tests inject a <see cref="FakeTimeProvider"/>-style substitute.</param>
    /// <param name="loggerFactory">Logger factory for structured diagnostic output.</param>
    /// <param name="ttl">Optional per-entry TTL. Defaults to <see cref="DefaultTtl"/>. A non-positive value is treated as the default.</param>
    public CachingOrchestrationStrategyProvider(
        IOrchestrationStrategyProvider inner,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        TimeSpan? ttl = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<CachingOrchestrationStrategyProvider>();
        Ttl = ttl is { } t && t > TimeSpan.Zero ? t : DefaultTtl;
    }

    /// <summary>
    /// The effective time-to-live applied to every cached entry.
    /// </summary>
    public TimeSpan Ttl { get; }

    /// <inheritdoc />
    public async Task<string?> GetStrategyKeyAsync(
        string unitId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitId))
        {
            // Mirror the inner provider's contract — a blank unitId never
            // resolves, and we don't want to clutter the cache with empties.
            return null;
        }

        var now = _timeProvider.GetUtcNow();

        if (_entries.TryGetValue(unitId, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Key;
        }

        var gate = _gates.GetOrAdd(unitId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check under the gate — another waiter may have populated it
            // while we were queued. This is the stampede guard: N concurrent
            // misses fan in to exactly one inner call.
            now = _timeProvider.GetUtcNow();
            if (_entries.TryGetValue(unitId, out cached) && cached.ExpiresAt > now)
            {
                return cached.Key;
            }

            var resolved = await _inner.GetStrategyKeyAsync(unitId, cancellationToken).ConfigureAwait(false);
            var expiresAt = _timeProvider.GetUtcNow() + Ttl;
            _entries[unitId] = new Entry(resolved, expiresAt);
            return resolved;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate(string unitId)
    {
        if (string.IsNullOrWhiteSpace(unitId))
        {
            return;
        }

        if (_entries.TryRemove(unitId, out _))
        {
            _logger.LogDebug(
                "Invalidated cached orchestration strategy for unit '{UnitId}'.",
                unitId);
        }
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        var count = _entries.Count;
        _entries.Clear();
        if (count > 0)
        {
            _logger.LogDebug(
                "Invalidated {Count} cached orchestration strategy entries.",
                count);
        }
    }

    private readonly record struct Entry(string? Key, DateTimeOffset ExpiresAt);
}