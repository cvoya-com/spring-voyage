// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitActor.GetPermissionInheritanceAsync"/> and
/// <see cref="UnitActor.SetPermissionInheritanceAsync"/> (#414). Covers the
/// state-absent default, upsert-then-read, and the "Inherit clears state"
/// semantics that mirrors the boundary actor's row-deletion pattern.
/// </summary>
public class UnitActorPermissionInheritanceTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly UnitActor _actor;

    public UnitActorPermissionInheritanceTests()
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
            .TryGetStateAsync<UnitPermissionInheritance>(
                StateKeys.UnitPermissionInheritance,
                Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitPermissionInheritance>(false, default));
    }

    [Fact]
    public async Task GetPermissionInheritanceAsync_NoState_ReturnsInherit()
    {
        var result = await _actor.GetPermissionInheritanceAsync(TestContext.Current.CancellationToken);

        result.ShouldBe(UnitPermissionInheritance.Inherit);
    }

    [Fact]
    public async Task SetPermissionInheritanceAsync_Isolated_PersistsToState()
    {
        UnitPermissionInheritance? captured = null;
        _stateManager
            .SetStateAsync(
                StateKeys.UnitPermissionInheritance,
                Arg.Do<UnitPermissionInheritance>(v => captured = v),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _actor.SetPermissionInheritanceAsync(
            UnitPermissionInheritance.Isolated,
            TestContext.Current.CancellationToken);

        captured.ShouldBe(UnitPermissionInheritance.Isolated);
    }

    [Fact]
    public async Task SetPermissionInheritanceAsync_Inherit_RemovesState()
    {
        // Writing the default clears state so the next read returns the
        // default via the absent-state path — symmetric with the boundary
        // actor's "empty = row deletion" pattern.
        await _actor.SetPermissionInheritanceAsync(
            UnitPermissionInheritance.Inherit,
            TestContext.Current.CancellationToken);

        await _stateManager.Received()
            .RemoveStateAsync(StateKeys.UnitPermissionInheritance, Arg.Any<CancellationToken>());
    }
}