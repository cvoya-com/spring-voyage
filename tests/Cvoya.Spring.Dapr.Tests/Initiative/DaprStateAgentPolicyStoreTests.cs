// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Initiative;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Initiative;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DaprStateAgentPolicyStore"/>. Uses an in-memory fake
/// <see cref="IStateStore"/> rather than a mock so the tests exercise end-to-end
/// round-tripping of policy and agent-unit assignment state.
/// </summary>
public class DaprStateAgentPolicyStoreTests
{
    private readonly InMemoryStateStore _stateStore = new();
    private readonly DaprStateAgentPolicyStore _sut;

    public DaprStateAgentPolicyStoreTests()
    {
        _sut = new DaprStateAgentPolicyStore(_stateStore);
    }

    [Fact]
    public async Task GetPolicyAsync_MissingKey_ReturnsDefaultPolicy()
    {
        var result = await _sut.GetPolicyAsync("agent:missing", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.MaxLevel.ShouldBe(InitiativeLevel.Passive);
    }

    [Fact]
    public async Task SetPolicyAsync_ThenGetPolicyAsync_RoundTrips()
    {
        var policy = new InitiativePolicy(MaxLevel: InitiativeLevel.Proactive);

        await _sut.SetPolicyAsync("agent:ada", policy, TestContext.Current.CancellationToken);
        var roundTripped = await _sut.GetPolicyAsync("agent:ada", TestContext.Current.CancellationToken);

        roundTripped.MaxLevel.ShouldBe(InitiativeLevel.Proactive);
    }

    [Fact]
    public async Task GetEffectiveLevelAsync_AgentPolicySet_ReturnsAgentMaxLevel()
    {
        await _sut.SetPolicyAsync("agent:ada", new InitiativePolicy(MaxLevel: InitiativeLevel.Proactive), TestContext.Current.CancellationToken);

        var level = await _sut.GetEffectiveLevelAsync("ada", TestContext.Current.CancellationToken);

        level.ShouldBe(InitiativeLevel.Proactive);
    }

    [Fact]
    public async Task GetEffectiveLevelAsync_NoAgentPolicy_FallsBackToUnitCeiling()
    {
        await _sut.SetPolicyAsync("unit:engineering", new InitiativePolicy(MaxLevel: InitiativeLevel.Attentive), TestContext.Current.CancellationToken);
        await _sut.SetAgentUnitAsync("ada", "engineering", TestContext.Current.CancellationToken);

        var level = await _sut.GetEffectiveLevelAsync("ada", TestContext.Current.CancellationToken);

        level.ShouldBe(InitiativeLevel.Attentive);
    }

    [Fact]
    public async Task GetEffectiveLevelAsync_NoAgentPolicyOrUnit_ReturnsPassive()
    {
        var level = await _sut.GetEffectiveLevelAsync("ada", TestContext.Current.CancellationToken);

        level.ShouldBe(InitiativeLevel.Passive);
    }

    [Fact]
    public async Task GetEffectiveLevelAsync_AgentPolicyOverridesUnit()
    {
        await _sut.SetPolicyAsync("agent:ada", new InitiativePolicy(MaxLevel: InitiativeLevel.Passive), TestContext.Current.CancellationToken);
        await _sut.SetPolicyAsync("unit:engineering", new InitiativePolicy(MaxLevel: InitiativeLevel.Proactive), TestContext.Current.CancellationToken);
        await _sut.SetAgentUnitAsync("ada", "engineering", TestContext.Current.CancellationToken);

        var level = await _sut.GetEffectiveLevelAsync("ada", TestContext.Current.CancellationToken);

        level.ShouldBe(InitiativeLevel.Passive);
    }

    [Fact]
    public async Task SetAgentUnitAsync_NullUnitId_ClearsAssignment()
    {
        await _sut.SetAgentUnitAsync("ada", "engineering", TestContext.Current.CancellationToken);
        await _sut.SetAgentUnitAsync("ada", null, TestContext.Current.CancellationToken);

        var unitId = await _sut.GetAgentUnitAsync("ada", TestContext.Current.CancellationToken);
        unitId.ShouldBeNull();
    }

    [Fact]
    public async Task GetAgentUnitAsync_AfterSet_ReturnsUnitId()
    {
        await _sut.SetAgentUnitAsync("ada", "engineering", TestContext.Current.CancellationToken);

        var unitId = await _sut.GetAgentUnitAsync("ada", TestContext.Current.CancellationToken);

        unitId.ShouldBe("engineering");
    }

    [Fact]
    public async Task SetAgentUnitAsync_AcceptsUnitPrefix_StripsIt()
    {
        await _sut.SetAgentUnitAsync("ada", "unit:engineering", TestContext.Current.CancellationToken);

        var unitId = await _sut.GetAgentUnitAsync("ada", TestContext.Current.CancellationToken);

        unitId.ShouldBe("engineering");
    }

    [Fact]
    public async Task PoliciesSurviveAcrossStoreInstances()
    {
        await _sut.SetPolicyAsync("agent:ada", new InitiativePolicy(MaxLevel: InitiativeLevel.Proactive), TestContext.Current.CancellationToken);

        // Simulate a process restart by creating a fresh store over the same state.
        var restartedSut = new DaprStateAgentPolicyStore(_stateStore);
        var policy = await restartedSut.GetPolicyAsync("agent:ada", TestContext.Current.CancellationToken);

        policy.MaxLevel.ShouldBe(InitiativeLevel.Proactive);
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
}
