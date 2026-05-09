// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Initiative;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Initiative;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DaprStateInitiativeBudgetTracker"/>. Exercises the daily
/// cap enforcement, UTC-day rollover, and restart-survival semantics using a
/// <see cref="FakeTimeProvider"/> and an in-memory state store double.
/// </summary>
public class DaprStateInitiativeBudgetTrackerTests
{
    private readonly InMemoryStateStore _stateStore = new();
    private readonly IAgentPolicyStore _policyStore = Substitute.For<IAgentPolicyStore>();
    private readonly MutableTimeProvider _timeProvider = new(new DateTimeOffset(2026, 4, 12, 23, 30, 0, TimeSpan.Zero));

    private DaprStateInitiativeBudgetTracker CreateSut()
        => new(_stateStore, _policyStore, _timeProvider);

    [Fact]
    public async Task TryConsumeAsync_FirstCallUnderCap_ReturnsTrueAndPersistsSpend()
    {
        ArrangePolicy(maxPerDay: 1.00m);
        var sut = CreateSut();

        var result = await sut.TryConsumeAsync("ada", 0.10m, TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
        var persisted = await _stateStore.GetAsync<DaprStateInitiativeBudgetTracker.AgentBudgetState>(
            "Initiative:Budget:ada", TestContext.Current.CancellationToken);
        persisted.ShouldNotBeNull();
        persisted!.Spent.ShouldBe(0.10m);
    }

    [Fact]
    public async Task TryConsumeAsync_ExceedingCap_ReturnsFalseAndDoesNotIncrementSpend()
    {
        ArrangePolicy(maxPerDay: 0.30m);
        var sut = CreateSut();

        (await sut.TryConsumeAsync("ada", 0.20m, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await sut.TryConsumeAsync("ada", 0.20m, TestContext.Current.CancellationToken)).ShouldBeFalse();

        var persisted = await _stateStore.GetAsync<DaprStateInitiativeBudgetTracker.AgentBudgetState>(
            "Initiative:Budget:ada", TestContext.Current.CancellationToken);
        persisted!.Spent.ShouldBe(0.20m);
    }

    [Fact]
    public async Task TryConsumeAsync_UsesDefaultCapWhenPolicyHasNone()
    {
        _policyStore.GetPolicyAsync("ada", Arg.Any<CancellationToken>())
            .Returns(new InitiativePolicy());
        var sut = CreateSut();

        var result = await sut.TryConsumeAsync("ada", DaprStateInitiativeBudgetTracker.DefaultMaxCostPerDay - 0.01m, TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task TryConsumeAsync_AcrossUtcDayBoundary_ResetsRunningTotal()
    {
        ArrangePolicy(maxPerDay: 0.30m);
        var sut = CreateSut();

        // Day 1: exhaust the cap.
        (await sut.TryConsumeAsync("ada", 0.20m, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await sut.TryConsumeAsync("ada", 0.20m, TestContext.Current.CancellationToken)).ShouldBeFalse();

        // Advance the clock past UTC midnight.
        _timeProvider.Advance(TimeSpan.FromHours(2));

        // Day 2: the running total must have rolled over so the same amount succeeds.
        (await sut.TryConsumeAsync("ada", 0.20m, TestContext.Current.CancellationToken)).ShouldBeTrue();

        var persisted = await _stateStore.GetAsync<DaprStateInitiativeBudgetTracker.AgentBudgetState>(
            "Initiative:Budget:ada", TestContext.Current.CancellationToken);
        persisted!.Day.ShouldBe(new DateTime(2026, 4, 13, 0, 0, 0, DateTimeKind.Utc));
        persisted.Spent.ShouldBe(0.20m);
    }

    [Fact]
    public async Task TryConsumeAsync_SurvivesProcessRestart()
    {
        ArrangePolicy(maxPerDay: 0.30m);

        // Pre-restart: consume some of the cap.
        var pre = CreateSut();
        (await pre.TryConsumeAsync("ada", 0.20m, TestContext.Current.CancellationToken)).ShouldBeTrue();

        // Simulate a restart: new tracker instance, same state store.
        var post = new DaprStateInitiativeBudgetTracker(_stateStore, _policyStore, _timeProvider);
        (await post.TryConsumeAsync("ada", 0.20m, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private void ArrangePolicy(decimal maxPerDay)
    {
        _policyStore.GetPolicyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new InitiativePolicy(Tier2: new Tier2Config(MaxCostPerDay: maxPerDay)));
    }

    /// <summary>
    /// In-memory <see cref="IStateStore"/> backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/>
    /// with System.Text.Json round-tripping to mirror real Dapr serialisation behaviour.
    /// </summary>
    private sealed class InMemoryStateStore : IStateStore
    {
        private readonly ConcurrentDictionary<string, string> _store = new();

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            if (_store.TryGetValue(key, out var json))
            {
                return Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<T>(json));
            }

            return Task.FromResult<T?>(default);
        }

        public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
        {
            _store[key] = System.Text.Json.JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            _store.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ContainsAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_store.ContainsKey(key));
    }

    /// <summary>
    /// Minimal mutable <see cref="TimeProvider"/> used for tests that need to advance time.
    /// </summary>
    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now + delta;
    }
}
