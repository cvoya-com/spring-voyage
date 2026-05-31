// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using System.Collections.Concurrent;

/// <summary>
/// Per-process <see cref="IOAuthResultStore"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Good enough for local
/// dev and single-host deployments; the cloud repo overrides with a
/// tenant-scoped persistent implementation.
///
/// <para>
/// Results expire after five minutes. Entries are purged opportunistically
/// on every <see cref="Put"/> call; a separate sweeper is not needed at the
/// OSS scale — the dictionary stays small.
/// </para>
/// </summary>
public class InMemoryOAuthResultStore : IOAuthResultStore
{
    private readonly ConcurrentDictionary<string, (OAuthCallbackResult Result, DateTimeOffset ExpiresAt)> _entries
        = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Put(string nonce, OAuthCallbackResult result)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(result);

        // Opportunistic sweep so stale entries don't accumulate.
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _entries)
        {
            if (kv.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(kv.Key, out _);
            }
        }

        _entries[nonce] = (result, now + TimeSpan.FromMinutes(5));
    }

    /// <inheritdoc />
    public OAuthCallbackResult? Consume(string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return null;
        }

        if (!_entries.TryRemove(nonce, out var entry))
        {
            return null;
        }

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return entry.Result;
    }
}
