// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

/// <summary>
/// Per-process in-memory <see cref="IOAuthStateStore"/>. Acceptable for
/// single-host deployments because the callback must hit the same host that
/// issued the authorize URL (GitHub's redirect goes to the exact URI the
/// caller registered, so a reverse proxy that pins to a single backend is
/// enough). Multi-host deployments should register a distributed
/// implementation before <c>AddCvoyaSpringConnectorGitHub</c> so
/// <c>TryAdd*</c> falls through.
///
/// <para>
/// Entries are purged opportunistically on every read/write; a separate
/// sweeper is not needed at the OSS scale — the dictionary stays small.
/// </para>
/// </summary>
public class InMemoryOAuthStateStore : IOAuthStateStore
{
    private readonly ConcurrentDictionary<string, OAuthStateEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a new in-memory store.
    /// </summary>
    public InMemoryOAuthStateStore(ILoggerFactory loggerFactory, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<InMemoryOAuthStateStore>();
    }

    /// <inheritdoc />
    public Task SaveAsync(OAuthStateEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        PurgeExpired();
        _entries[entry.State] = entry;
        _logger.LogDebug(
            "Stored OAuth pending-authorization state; expires_at={ExpiresAt:o}",
            entry.ExpiresAt);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<OAuthStateEntry?> ConsumeAsync(string state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return Task.FromResult<OAuthStateEntry?>(null);
        }

        PurgeExpired();

        if (!_entries.TryRemove(state, out var entry))
        {
            _logger.LogDebug("OAuth state consume miss (unknown or already-consumed state)");
            return Task.FromResult<OAuthStateEntry?>(null);
        }

        if (entry.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            _logger.LogInformation(
                "OAuth state expired before callback (expires_at={ExpiresAt:o})",
                entry.ExpiresAt);
            return Task.FromResult<OAuthStateEntry?>(null);
        }

        return Task.FromResult<OAuthStateEntry?>(entry);
    }

    private void PurgeExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var kv in _entries)
        {
            if (kv.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(kv.Key, out _);
            }
        }
    }
}