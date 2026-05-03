// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitActor.GetBoundaryAsync"/> and
/// <see cref="UnitActor.SetBoundaryAsync"/> (#413). Covers empty-state
/// defaults, upsert-then-read, and the "empty boundary clears state"
/// semantics.
/// </summary>
public class UnitActorBoundaryTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly UnitActor _actor;

    public UnitActorBoundaryTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
        });
        _actor = new UnitActor(
            host,
            loggerFactory,
            Substitute.For<IOrchestrationStrategy>(),
            Substitute.For<IActivityEventBus>(),
            Substitute.For<IDirectoryService>(),
            Substitute.For<IActorProxyFactory>());

        var field = typeof(Actor).GetField("<StateManager>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(_actor, _stateManager);

        _stateManager
            .TryGetStateAsync<UnitBoundary>(StateKeys.UnitBoundary, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitBoundary>(false, default!));
    }

    [Fact]
    public async Task GetBoundaryAsync_NoState_ReturnsEmpty()
    {
        var result = await _actor.GetBoundaryAsync(TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();
        result.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task SetBoundaryAsync_NonEmpty_PersistsToState()
    {
        UnitBoundary? captured = null;
        _stateManager
            .SetStateAsync(
                StateKeys.UnitBoundary,
                Arg.Do<UnitBoundary>(v => captured = v),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var boundary = new UnitBoundary(
            Opacities: new[] { new BoundaryOpacityRule(DomainPattern: "secret-*") });

        await _actor.SetBoundaryAsync(boundary, TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Opacities!.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SetBoundaryAsync_Empty_RemovesState()
    {
        await _actor.SetBoundaryAsync(UnitBoundary.Empty, TestContext.Current.CancellationToken);

        await _stateManager.Received().RemoveStateAsync(StateKeys.UnitBoundary, Arg.Any<CancellationToken>());
    }
}