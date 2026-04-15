// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using System.Collections.Concurrent;

/// <summary>
/// Per-process <see cref="IOAuthSessionStore"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Good enough for local
/// dev and single-host deployments; the cloud repo overrides with a
/// tenant-scoped persistent implementation.
/// </summary>
public class InMemoryOAuthSessionStore : IOAuthSessionStore
{
    private readonly ConcurrentDictionary<string, OAuthSession> _sessions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(OAuthSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions[session.SessionId] = session;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<OAuthSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult<OAuthSession?>(null);
        }
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult<OAuthSession?>(session);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _sessions.TryRemove(sessionId, out _);
        }
        return Task.CompletedTask;
    }
}