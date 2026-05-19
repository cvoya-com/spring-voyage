// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk.Tests;

using Cvoya.Spring.AgentSdk;

using Shouldly;

using Xunit;

/// <summary>
/// Unit coverage for <see cref="ProgressRateLimiter"/> (#2493).
/// </summary>
public class ProgressRateLimiterTests
{
    [Fact]
    public void Burst_AllowsUpToBurstCallsImmediately()
    {
        var limiter = new ProgressRateLimiter(ratePerSecond: 1.0, burst: 5);
        for (int i = 0; i < 5; i++)
        {
            limiter.TryAcquire("subject", "progress").ShouldBeTrue($"call {i}");
        }
    }

    [Fact]
    public void Burst_ExhaustedThenRateLimited()
    {
        var limiter = new ProgressRateLimiter(ratePerSecond: 0.01, burst: 2);
        limiter.TryAcquire("subject", "progress").ShouldBeTrue();
        limiter.TryAcquire("subject", "progress").ShouldBeTrue();
        limiter.TryAcquire("subject", "progress").ShouldBeFalse();
    }

    [Fact]
    public void PerPair_BucketsAreIsolated()
    {
        var limiter = new ProgressRateLimiter(ratePerSecond: 0.01, burst: 1);
        limiter.TryAcquire("subject-a", "progress").ShouldBeTrue();
        limiter.TryAcquire("subject-a", "progress").ShouldBeFalse();
        // Different subject — fresh bucket.
        limiter.TryAcquire("subject-b", "progress").ShouldBeTrue();
        // Different kind, same subject — fresh bucket.
        limiter.TryAcquire("subject-a", "tool_call").ShouldBeTrue();
    }

    [Fact]
    public void ThousandPerSecond_CappedByBurstPlusSustained()
    {
        // Acceptance criterion: 1000x/sec → ≤ burst + sustained × duration.
        const double rate = 5.0;
        const int burst = 20;
        var limiter = new ProgressRateLimiter(rate, burst);

        var start = DateTime.UtcNow;
        int accepted = 0;
        int attempts = 0;
        // Run for ~50 ms with bursty calls.
        while ((DateTime.UtcNow - start).TotalMilliseconds < 50)
        {
            attempts++;
            if (limiter.TryAcquire("subject", "progress"))
            {
                accepted++;
            }
        }
        var elapsed = (DateTime.UtcNow - start).TotalSeconds;

        attempts.ShouldBeGreaterThan(50);
        // Upper bound: burst + rate × actual elapsed + slack.
        var upperBound = burst + (int)(rate * elapsed) + 2;
        accepted.ShouldBeLessThanOrEqualTo(upperBound,
            $"accepted={accepted} attempts={attempts} elapsed={elapsed:F3}s");
    }

    [Fact]
    public void OnDrop_InvokedOncePer30sPerPair()
    {
        var dropCount = 0;
        var limiter = new ProgressRateLimiter(
            ratePerSecond: 0.01,
            burst: 1,
            onDrop: (_, _) => Interlocked.Increment(ref dropCount));

        limiter.TryAcquire("subject", "progress").ShouldBeTrue();
        limiter.TryAcquire("subject", "progress").ShouldBeFalse();
        limiter.TryAcquire("subject", "progress").ShouldBeFalse();
        limiter.TryAcquire("subject", "progress").ShouldBeFalse();
        // First drop logs; subsequent within the 30 s window do not.
        dropCount.ShouldBe(1);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveRate()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new ProgressRateLimiter(ratePerSecond: 0, burst: 5));
        Should.Throw<ArgumentOutOfRangeException>(
            () => new ProgressRateLimiter(ratePerSecond: -1, burst: 5));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveBurst()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new ProgressRateLimiter(ratePerSecond: 5, burst: 0));
        Should.Throw<ArgumentOutOfRangeException>(
            () => new ProgressRateLimiter(ratePerSecond: 5, burst: -1));
    }
}
