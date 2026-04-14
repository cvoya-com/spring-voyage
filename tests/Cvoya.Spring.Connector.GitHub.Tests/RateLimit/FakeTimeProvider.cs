// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.RateLimit;

using System.Collections.Concurrent;

/// <summary>
/// Minimal deterministic <see cref="TimeProvider"/> for the rate-limit /
/// retry tests. Clock is frozen until <see cref="Advance"/> is called, and
/// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> waits
/// strictly on the virtual clock. Kept in the test project so production code
/// stays free of test scaffolding.
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<FakeTimer, byte> _timers = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state, dueTime, period);
        _timers[timer] = 0;
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_lock)
        {
            _now += by;
            target = _now;
        }

        foreach (var timer in _timers.Keys.ToArray())
        {
            timer.TryFire(target);
        }
    }

    internal void Remove(FakeTimer timer) => _timers.TryRemove(timer, out _);

    internal sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset _dueAt;
        private TimeSpan _period;
        private bool _disposed;

        public FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _period = period;
            _dueAt = due == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.GetUtcNow() + due;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _owner.GetUtcNow() + dueTime;
            return true;
        }

        public void TryFire(DateTimeOffset now)
        {
            while (!_disposed && now >= _dueAt)
            {
                _callback(_state);
                if (_period <= TimeSpan.Zero)
                {
                    _dueAt = DateTimeOffset.MaxValue;
                    break;
                }
                _dueAt += _period;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}