// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Cloning;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Cloning;

using Shouldly;

using Xunit;

/// <summary>
/// Round-trip tests for <see cref="StateStoreAgentCloningPolicyRepository"/>.
/// Uses a JSON-round-tripping fake <see cref="IStateStore"/> so the tests
/// exercise the same serialisation path the Dapr state store follows at
/// runtime.
/// </summary>
public class StateStoreAgentCloningPolicyRepositoryTests
{
    private readonly InMemoryStateStore _stateStore = new();
    private readonly StateStoreAgentCloningPolicyRepository _sut;

    public StateStoreAgentCloningPolicyRepositoryTests()
    {
        _sut = new StateStoreAgentCloningPolicyRepository(_stateStore);
    }

    [Fact]
    public async Task GetAsync_MissingKey_ReturnsEmptyPolicy()
    {
        var result = await _sut.GetAsync(
            CloningPolicyScope.Agent, "unknown-agent", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task SetAsync_RoundTripsAllSlots_ForAgentScope()
    {
        var policy = new AgentCloningPolicy(
            AllowedPolicies: new[] { CloningPolicy.EphemeralNoMemory, CloningPolicy.EphemeralWithMemory },
            AllowedAttachmentModes: new[] { AttachmentMode.Detached },
            MaxClones: 5,
            MaxDepth: 1,
            Budget: 42m);

        await _sut.SetAsync(CloningPolicyScope.Agent, "ada", policy, TestContext.Current.CancellationToken);

        var loaded = await _sut.GetAsync(
            CloningPolicyScope.Agent, "ada", TestContext.Current.CancellationToken);

        loaded.AllowedPolicies.ShouldBe(policy.AllowedPolicies);
        loaded.AllowedAttachmentModes.ShouldBe(policy.AllowedAttachmentModes);
        loaded.MaxClones.ShouldBe(5);
        loaded.MaxDepth.ShouldBe(1);
        loaded.Budget.ShouldBe(42m);
    }

    [Fact]
    public async Task SetAsync_TenantScope_UsesTenantKeyPrefix()
    {
        var policy = new AgentCloningPolicy(MaxClones: 10);

        await _sut.SetAsync(
            CloningPolicyScope.Tenant, "acme", policy, TestContext.Current.CancellationToken);

        // Agent scope for the same id should still be empty — distinct namespaces.
        var agentScope = await _sut.GetAsync(
            CloningPolicyScope.Agent, "acme", TestContext.Current.CancellationToken);
        agentScope.IsEmpty.ShouldBeTrue();

        var tenantScope = await _sut.GetAsync(
            CloningPolicyScope.Tenant, "acme", TestContext.Current.CancellationToken);
        tenantScope.MaxClones.ShouldBe(10);
    }

    [Fact]
    public async Task SetAsync_EmptyPolicy_DeletesTheRow()
    {
        await _sut.SetAsync(
            CloningPolicyScope.Agent, "ada",
            new AgentCloningPolicy(MaxClones: 3),
            TestContext.Current.CancellationToken);

        await _sut.SetAsync(
            CloningPolicyScope.Agent, "ada",
            AgentCloningPolicy.Empty,
            TestContext.Current.CancellationToken);

        // Empty persisted means "no row" — hit the IStateStore directly to
        // confirm the delete actually happened rather than storing a
        // serialized Empty payload.
        var raw = await _stateStore.ContainsAsync(
            $"{StateKeys.AgentCloningPolicy}:ada", TestContext.Current.CancellationToken);
        raw.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotent()
    {
        // Arrange — nothing persisted.
        await _sut.DeleteAsync(
            CloningPolicyScope.Agent, "ada", TestContext.Current.CancellationToken);

        // Act — again.
        await _sut.DeleteAsync(
            CloningPolicyScope.Agent, "ada", TestContext.Current.CancellationToken);

        // Assert — no exception, GET still returns Empty.
        var loaded = await _sut.GetAsync(
            CloningPolicyScope.Agent, "ada", TestContext.Current.CancellationToken);
        loaded.IsEmpty.ShouldBeTrue();
    }

    /// <summary>
    /// JSON-round-tripping in-memory IStateStore (same shape the initiative
    /// fakes use).
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