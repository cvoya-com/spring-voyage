// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Orchestration;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="CachingOrchestrationStrategyProvider"/> (#518). The
/// decorator wraps an inner <see cref="IOrchestrationStrategyProvider"/>
/// (normally <see cref="DbOrchestrationStrategyProvider"/>) so steady-state
/// traffic to a unit resolves its orchestration strategy from memory
/// instead of re-reading Postgres on every domain message. These tests
/// cover the four must-have behaviours enumerated on #518:
/// steady-state single-read, cold miss, post-write invalidation, and
/// stampede coalescing.
/// </summary>
public class CachingOrchestrationStrategyProviderTests
{
    [Fact]
    public async Task GetStrategyKeyAsync_RepeatedReadsSameUnit_HitsInnerOnce()
    {
        // Steady-state: N domain messages → ≤1 DB read.
        var inner = Substitute.For<IOrchestrationStrategyProvider>();
        inner.GetStrategyKeyAsync("triage", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("label-routed"));

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sut = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance);

        for (var i = 0; i < 50; i++)
        {
            var key = await sut.GetStrategyKeyAsync("triage", TestContext.Current.CancellationToken);
            key.ShouldBe("label-routed");
        }

        await inner.Received(1).GetStrategyKeyAsync("triage", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStrategyKeyAsync_NegativeResult_IsAlsoCached()
    {
        // The common case is "no orchestration.strategy declared" — that
        // negative result must be cached too or the hot path still pays a
        // DB round-trip on every message.
        var inner = Substitute.For<IOrchestrationStrategyProvider>();
        inner.GetStrategyKeyAsync("plain", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sut = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance);

        for (var i = 0; i < 10; i++)
        {
            var key = await sut.GetStrategyKeyAsync("plain", TestContext.Current.CancellationToken);
            key.ShouldBeNull();
        }

        await inner.Received(1).GetStrategyKeyAsync("plain", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStrategyKeyAsync_ColdUnit_IssuesExactlyOneInnerRead()
    {
        var inner = Substitute.For<IOrchestrationStrategyProvider>();
        inner.GetStrategyKeyAsync("cold", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("workflow"));

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sut = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance);

        var key = await sut.GetStrategyKeyAsync("cold", TestContext.Current.CancellationToken);

        key.ShouldBe("workflow");
        await inner.Received(1).GetStrategyKeyAsync("cold", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStrategyKeyAsync_AfterTtlElapses_RefreshesFromInner()
    {
        // When a cross-process write happens, the in-process cache heals
        // itself within the TTL — no invalidation signal required.
        var inner = Substitute.For<IOrchestrationStrategyProvider>();
        inner.GetStrategyKeyAsync("drifty", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<string?>("ai"),
                Task.FromResult<string?>("label-routed"));

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sut = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance);

        (await sut.GetStrategyKeyAsync("drifty", TestContext.Current.CancellationToken))
            .ShouldBe("ai");

        // Advance past the TTL — the next read must re-query the inner
        // provider and pick up the new value.
        time.Advance(sut.Ttl + TimeSpan.FromSeconds(1));

        (await sut.GetStrategyKeyAsync("drifty", TestContext.Current.CancellationToken))
            .ShouldBe("label-routed");
        await inner.Received(2).GetStrategyKeyAsync("drifty", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalidate_ExistingEntry_NextReadGoesBackToInner()
    {
        // Simulates UnitCreationService.PersistUnitDefinitionOrchestrationAsync
        // writing the row and then firing the invalidator. The next message
        // dispatched to the unit must see the new value without waiting out
        // the TTL.
        var inner = Substitute.For<IOrchestrationStrategyProvider>();
        inner.GetStrategyKeyAsync("written", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<string?>("ai"),
                Task.FromResult<string?>("label-routed"));

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sut = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance);

        (await sut.GetStrategyKeyAsync("written", TestContext.Current.CancellationToken))
            .ShouldBe("ai");

        // Write path fires invalidation — no time advance.
        sut.Invalidate("written");

        (await sut.GetStrategyKeyAsync("written", TestContext.Current.CancellationToken))
            .ShouldBe("label-routed");
        await inner.Received(2).GetStrategyKeyAsync("written", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalidate_MissingEntry_IsNoOp()
    {
        // Invalidating a unit that was never cached must never throw — the
        // write path must be able to fire the hook unconditionally.
        var inner = Substitute.For<IOrchestrationStrategyProvider>();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sut = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance);

        Should.NotThrow(() => sut.Invalidate("never-seen"));
        Should.NotThrow(() => sut.Invalidate(string.Empty));
        Should.NotThrow(() => sut.Invalidate("  "));
    }

    [Fact]
    public async Task InvalidateAll_DropsEveryEntry()
    {
        var inner = Substitute.For<IOrchestrationStrategyProvider>();
        inner.GetStrategyKeyAsync("a", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<string?>("ai"),
                Task.FromResult<string?>("ai"));
        inner.GetStrategyKeyAsync("b", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<string?>("workflow"),
                Task.FromResult<string?>("workflow"));

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sut = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance);

        _ = await sut.GetStrategyKeyAsync("a", TestContext.Current.CancellationToken);
        _ = await sut.GetStrategyKeyAsync("b", TestContext.Current.CancellationToken);

        sut.InvalidateAll();

        _ = await sut.GetStrategyKeyAsync("a", TestContext.Current.CancellationToken);
        _ = await sut.GetStrategyKeyAsync("b", TestContext.Current.CancellationToken);
        await inner.Received(2).GetStrategyKeyAsync("a", Arg.Any<CancellationToken>());
        await inner.Received(2).GetStrategyKeyAsync("b", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStrategyKeyAsync_ConcurrentMissesOnSameUnit_InnerCalledOnce()
    {
        // Stampede guard: N concurrent cold readers must collapse into
        // exactly one inner call — the semaphore gates them so the first
        // fills the cache and every subsequent waiter reads the populated
        // entry under the gate.
        using var gate = new ManualResetEventSlim(initialState: false);
        var inner = new GatedProvider(gate, resultKey: "label-routed");

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sut = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance);

        var tasks = new Task<string?>[32];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(
                () => sut.GetStrategyKeyAsync("stamped", TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);
        }

        // Give every task a chance to queue behind the gate before releasing.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        gate.Set();

        var results = await Task.WhenAll(tasks);
        results.ShouldAllBe(r => r == "label-routed");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetStrategyKeyAsync_InnerThrows_NotCached()
    {
        // A transient inner failure must surface to the caller and leave
        // the cache empty so the next attempt retries. Caching the failure
        // would turn a blip into sustained downtime for the unit.
        var inner = Substitute.For<IOrchestrationStrategyProvider>();
        var failure = new InvalidOperationException("boom");
        inner.GetStrategyKeyAsync("flaky", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<string?>(failure),
                Task.FromResult<string?>("ai"));

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sut = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance);

        await Should.ThrowAsync<InvalidOperationException>(
            () => sut.GetStrategyKeyAsync("flaky", TestContext.Current.CancellationToken));

        // Second call — inner now returns a real value, and it should be
        // read since nothing was cached.
        (await sut.GetStrategyKeyAsync("flaky", TestContext.Current.CancellationToken))
            .ShouldBe("ai");
    }

    [Fact]
    public async Task GetStrategyKeyAsync_BlankUnitId_ReturnsNullWithoutTouchingInner()
    {
        var inner = Substitute.For<IOrchestrationStrategyProvider>();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sut = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance);

        (await sut.GetStrategyKeyAsync(string.Empty, TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await sut.GetStrategyKeyAsync("   ", TestContext.Current.CancellationToken))
            .ShouldBeNull();

        await inner.DidNotReceive().GetStrategyKeyAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Constructor_NonPositiveTtl_FallsBackToDefault()
    {
        var inner = Substitute.For<IOrchestrationStrategyProvider>();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);

        var withZero = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance, TimeSpan.Zero);
        var withNegative = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance, TimeSpan.FromSeconds(-5));
        var withNull = new CachingOrchestrationStrategyProvider(
            inner, time, NullLoggerFactory.Instance, ttl: null);

        withZero.Ttl.ShouldBe(CachingOrchestrationStrategyProvider.DefaultTtl);
        withNegative.Ttl.ShouldBe(CachingOrchestrationStrategyProvider.DefaultTtl);
        withNull.Ttl.ShouldBe(CachingOrchestrationStrategyProvider.DefaultTtl);
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose "now" is under test control so the
    /// TTL boundary can be crossed deterministically without wall-clock
    /// sleeps. Keeps the suite fast and stable on CI.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _nowTicks;

        public ManualTimeProvider(DateTimeOffset start)
        {
            _nowTicks = start.UtcTicks;
        }

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _nowTicks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _nowTicks, by.Ticks);
    }

    /// <summary>
    /// Inner-provider test double that blocks on a
    /// <see cref="ManualResetEventSlim"/> until the test explicitly releases
    /// it. Used to set up the stampede scenario — every concurrent miss
    /// piles up before the first inner call can complete.
    /// </summary>
    private sealed class GatedProvider : IOrchestrationStrategyProvider
    {
        private readonly ManualResetEventSlim _gate;
        private readonly string? _resultKey;
        private int _callCount;

        public GatedProvider(ManualResetEventSlim gate, string? resultKey)
        {
            _gate = gate;
            _resultKey = resultKey;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<string?> GetStrategyKeyAsync(string unitId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            // xUnit1051: the cancellation token here is the one the decorator
            // propagates; we deliberately forward it. No test-owned token is
            // in scope at this point.
#pragma warning disable xUnit1051
            await Task.Run(() => _gate.Wait(cancellationToken), cancellationToken).ConfigureAwait(false);
#pragma warning restore xUnit1051
            return _resultKey;
        }
    }
}