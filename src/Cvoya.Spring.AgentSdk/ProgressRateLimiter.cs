// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Collections.Concurrent;

/// <summary>
/// Token-bucket rate limiter for SDK-emitted telemetry events
/// (issue #2493). Mirrors the Python SDK's
/// <c>spring_voyage_agent_sdk.rate_limit.ProgressRateLimiter</c>.
/// </summary>
/// <remarks>
/// <para>
/// One bucket per <c>(subject_uuid, event_kind)</c> pair. Tokens
/// accumulate at <see cref="RatePerSecond"/> up to <see cref="Burst"/>;
/// each call to <see cref="TryAcquire"/> consumes one token. Excess
/// events are dropped silently — callers MUST drop the event on a
/// <c>false</c> return.
/// </para>
/// <para>
/// Defaults: 5 events/second sustained, 20 burst. Env-overridable via
/// <c>SV_PROGRESS_RATE_LIMIT_RPS</c> /
/// <c>SV_PROGRESS_RATE_LIMIT_BURST</c>.
/// </para>
/// </remarks>
public sealed class ProgressRateLimiter
{
    /// <summary>Default sustained event rate (5 events/sec).</summary>
    public const double DefaultRatePerSecond = 5.0;

    /// <summary>Default burst capacity (20 events).</summary>
    public const int DefaultBurst = 20;

    /// <summary>Env var that overrides <see cref="RatePerSecond"/>.</summary>
    public const string RateEnvVar = "SV_PROGRESS_RATE_LIMIT_RPS";

    /// <summary>Env var that overrides <see cref="Burst"/>.</summary>
    public const string BurstEnvVar = "SV_PROGRESS_RATE_LIMIT_BURST";

    private static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(30);

    private readonly double _rate;
    private readonly int _burst;
    private readonly ConcurrentDictionary<(string Subject, string Kind), TokenBucket> _buckets = new();
    private readonly Action<string, string>? _onDrop;

    /// <summary>Constructs a limiter with explicit rate / burst.</summary>
    public ProgressRateLimiter(double ratePerSecond, int burst, Action<string, string>? onDrop = null)
    {
        if (ratePerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ratePerSecond), ratePerSecond, "Rate must be positive.");
        }
        if (burst <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(burst), burst, "Burst must be positive.");
        }

        _rate = ratePerSecond;
        _burst = burst;
        _onDrop = onDrop;
    }

    /// <summary>Constructs a limiter from the env vars, falling back to defaults.</summary>
    public static ProgressRateLimiter FromEnvironment(Action<string, string>? onDrop = null)
    {
        var rate = ParseEnvDouble(RateEnvVar, DefaultRatePerSecond);
        var burst = ParseEnvInt(BurstEnvVar, DefaultBurst);
        return new ProgressRateLimiter(rate, burst, onDrop);
    }

    /// <summary>Configured sustained rate, events per second.</summary>
    public double RatePerSecond => _rate;

    /// <summary>Configured burst capacity, events.</summary>
    public int Burst => _burst;

    /// <summary>
    /// Attempt to emit one event for <paramref name="subjectUuid"/> /
    /// <paramref name="eventKind"/>. Returns <c>true</c> if the event is
    /// allowed; <c>false</c> if the bucket is empty.
    /// </summary>
    public bool TryAcquire(string subjectUuid, string eventKind)
    {
        ArgumentNullException.ThrowIfNull(subjectUuid);
        ArgumentNullException.ThrowIfNull(eventKind);

        var bucket = _buckets.GetOrAdd(
            (subjectUuid, eventKind),
            _ => new TokenBucket(_rate, _burst));

        if (bucket.TryAcquire())
        {
            return true;
        }

        if (bucket.ShouldLogDrop())
        {
            _onDrop?.Invoke(subjectUuid, eventKind);
        }
        return false;
    }

    private static double ParseEnvDouble(string name, double defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }

    private static int ParseEnvInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }

    private sealed class TokenBucket
    {
        private readonly double _rate;
        private readonly double _burst;
        private double _tokens;
        private long _lastRefillTicks;
        private long _lastWarningTicks;
        private readonly object _lock = new();

        public TokenBucket(double rate, int burst)
        {
            _rate = rate;
            _burst = burst;
            _tokens = burst;
            _lastRefillTicks = Environment.TickCount64;
            _lastWarningTicks = 0;
        }

        public bool TryAcquire()
        {
            lock (_lock)
            {
                var now = Environment.TickCount64;
                var elapsedSeconds = Math.Max(0.0, (now - _lastRefillTicks) / 1000.0);
                _tokens = Math.Min(_burst, _tokens + elapsedSeconds * _rate);
                _lastRefillTicks = now;

                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return true;
                }
                return false;
            }
        }

        public bool ShouldLogDrop()
        {
            lock (_lock)
            {
                var now = Environment.TickCount64;
                if (now - _lastWarningTicks >= (long)WarningInterval.TotalMilliseconds)
                {
                    _lastWarningTicks = now;
                    return true;
                }
                return false;
            }
        }
    }
}
