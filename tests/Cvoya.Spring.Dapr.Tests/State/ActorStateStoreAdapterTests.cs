// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.State;

using Cvoya.Spring.Dapr.State;

using FluentAssertions;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ActorStateStoreAdapter"/> verifying correct delegation to <see cref="IActorStateManager"/>.
/// </summary>
public class ActorStateStoreAdapterTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ActorStateStoreAdapter _sut;

    public ActorStateStoreAdapterTests()
    {
        _sut = new ActorStateStoreAdapter(_stateManager);
    }

    [Fact]
    public async Task GetAsync_ExistingKey_ReturnsValue()
    {
        _stateManager.TryGetStateAsync<string>("key1", Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(true, "hello"));

        var result = await _sut.GetAsync<string>("key1", TestContext.Current.CancellationToken);

        result.Should().Be("hello");
    }

    [Fact]
    public async Task GetAsync_MissingKey_ReturnsDefault()
    {
        _stateManager.TryGetStateAsync<string>("missing", Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(false, default!));

        var result = await _sut.GetAsync<string>("missing", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_MissingValueType_ReturnsDefault()
    {
        _stateManager.TryGetStateAsync<int>("missing-int", Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<int>(false, default));

        var result = await _sut.GetAsync<int>("missing-int", TestContext.Current.CancellationToken);

        result.Should().Be(0);
    }

    [Fact]
    public async Task SetAsync_DelegatesToStateManager()
    {
        await _sut.SetAsync("key1", 42, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync("key1", 42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_DelegatesToTryRemoveState()
    {
        await _sut.DeleteAsync("key1", TestContext.Current.CancellationToken);

        await _stateManager.Received(1).TryRemoveStateAsync("key1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContainsAsync_ExistingKey_ReturnsTrue()
    {
        _stateManager.ContainsStateAsync("key1", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.ContainsAsync("key1", TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ContainsAsync_MissingKey_ReturnsFalse()
    {
        _stateManager.ContainsStateAsync("missing", Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.ContainsAsync("missing", TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }
}