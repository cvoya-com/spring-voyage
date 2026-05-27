// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Auth.OAuth;

using System.Collections.Concurrent;

/// <summary>
/// Default in-memory <see cref="ISlackOAuthStateStore"/>. Sufficient
/// for OSS single-host deployments where the authorize call and the
/// callback land on the same process. Cloud overlays substitute a
/// Redis-backed implementation for multi-host distribution.
/// </summary>
public class InMemorySlackOAuthStateStore : ISlackOAuthStateStore
{
    private readonly ConcurrentDictionary<string, SlackOAuthStateEntry> _entries = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the store.</summary>
    public InMemorySlackOAuthStateStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task SaveAsync(SlackOAuthStateEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries[entry.State] = entry;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SlackOAuthStateEntry?> ConsumeAsync(string state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return Task.FromResult<SlackOAuthStateEntry?>(null);
        }

        if (!_entries.TryRemove(state, out var entry))
        {
            return Task.FromResult<SlackOAuthStateEntry?>(null);
        }

        if (entry.ExpiresAt < _timeProvider.GetUtcNow())
        {
            return Task.FromResult<SlackOAuthStateEntry?>(null);
        }

        return Task.FromResult<SlackOAuthStateEntry?>(entry);
    }
}
