// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Collections.Concurrent;

public class OrchestrationDepthCounter
{
    public const int DefaultMaxDepth = 8;

    private readonly ConcurrentDictionary<Guid, int> _depthByThread = new();
    private readonly int _maxDepth;

    public OrchestrationDepthCounter()
        : this(DefaultMaxDepth)
    {
    }

    public OrchestrationDepthCounter(int maxDepth)
    {
        if (maxDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "Maximum orchestration depth must be greater than zero.");
        }

        _maxDepth = maxDepth;
    }

    public int Current(Guid threadId) =>
        _depthByThread.TryGetValue(threadId, out var current)
            ? current
            : 0;

    public IDisposable Increment(Guid threadId)
    {
        while (true)
        {
            var current = Current(threadId);
            if (current >= _maxDepth)
            {
                throw new OrchestrationException(
                    OrchestrationException.RejectCodes.OrchestrationDepthExceeded,
                    $"Orchestration depth budget exceeded for thread '{threadId}'.");
            }

            if (current == 0)
            {
                if (_depthByThread.TryAdd(threadId, 1))
                {
                    return new Scope(this, threadId);
                }

                continue;
            }

            if (_depthByThread.TryUpdate(threadId, current + 1, current))
            {
                return new Scope(this, threadId);
            }
        }
    }

    private void Decrement(Guid threadId)
    {
        while (true)
        {
            if (!_depthByThread.TryGetValue(threadId, out var current))
            {
                return;
            }

            if (current <= 1)
            {
                var removed = ((ICollection<KeyValuePair<Guid, int>>)_depthByThread)
                    .Remove(new KeyValuePair<Guid, int>(threadId, current));
                if (removed)
                {
                    return;
                }

                continue;
            }

            if (_depthByThread.TryUpdate(threadId, current - 1, current))
            {
                return;
            }
        }
    }

    private sealed class Scope(OrchestrationDepthCounter owner, Guid threadId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Decrement(threadId);
            }
        }
    }
}