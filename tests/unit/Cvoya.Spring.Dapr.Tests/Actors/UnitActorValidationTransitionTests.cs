// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Tests.TestHelpers;
using Cvoya.Spring.Dapr.Units;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the five new lifecycle edges introduced in T-02 (#944):
/// Draft→Validating, Validating→Stopped, Validating→Error, Error→Validating,
/// Stopped→Validating. Mirrors the transition-test style in
/// <see cref="UnitActorTests"/>; the orchestrator that drives the probe run
/// (start a Dapr workflow, persist LastValidationRunId, write
/// LastValidationErrorJson on failure) lands in T-05 and is out of scope here.
/// </summary>
public class UnitActorValidationTransitionTests
{
    private const string TestUnitActorId = "test-unit";

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IRuntimeInvocationPath _runtimeInvocationPath = Substitute.For<IRuntimeInvocationPath>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly UnitActor _actor;

    public UnitActorValidationTransitionTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId(TestUnitActorId)
        });
        _actor = new UnitActor(
            host,
            _loggerFactory,
            _runtimeInvocationPath,
            _activityEventBus,
            _directoryService,
            _actorProxyFactory,
            new UnitStateCoordinator(new InMemoryUnitLiveConfigStore(), Substitute.For<ILogger<UnitStateCoordinator>>()),
            new InMemoryUnitMemberGraphStore());
        SetStateManager(_actor, _stateManager);

        // Default: no persisted status -> Draft.
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(false, default));
    }

    private static void SetStateManager(Actor actor, IActorStateManager stateManager)
    {
        var field = typeof(Actor).GetField("<StateManager>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (field is not null)
        {
            field.SetValue(actor, stateManager);
        }
        else
        {
            var prop = typeof(Actor).GetProperty("StateManager");
            prop?.SetValue(actor, stateManager);
        }
    }

    private void WithCurrentStatus(LifecycleStatus current)
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, current));
    }

    // --- Allowed edges ---

    [Fact]
    public async Task TransitionAsync_DraftToValidating_Succeeds()
    {
        WithCurrentStatus(LifecycleStatus.Draft);

        var result = await _actor.TransitionAsync(LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Validating);
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.UnitLifecycleStatus,
            LifecycleStatus.Validating,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_ValidatingToStopped_Succeeds()
    {
        WithCurrentStatus(LifecycleStatus.Validating);

        var result = await _actor.TransitionAsync(LifecycleStatus.Stopped, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Stopped);
    }

    [Fact]
    public async Task TransitionAsync_ValidatingToError_Succeeds()
    {
        WithCurrentStatus(LifecycleStatus.Validating);

        var result = await _actor.TransitionAsync(LifecycleStatus.Error, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Error);
    }

    [Fact]
    public async Task TransitionAsync_ErrorToValidating_Succeeds()
    {
        WithCurrentStatus(LifecycleStatus.Error);

        var result = await _actor.TransitionAsync(LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Validating);
    }

    [Fact]
    public async Task TransitionAsync_StoppedToValidating_Succeeds()
    {
        WithCurrentStatus(LifecycleStatus.Stopped);

        var result = await _actor.TransitionAsync(LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Validating);
    }

    // --- Disallowed edges — only non-Draft, non-Stopped, non-Error states may
    // not enter Validating. Running/Starting/Stopping must first transition
    // through Stopped before requesting revalidation. ---

    [Fact]
    public async Task TransitionAsync_RunningToValidating_Rejected()
    {
        WithCurrentStatus(LifecycleStatus.Running);

        var result = await _actor.TransitionAsync(LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Running);
        result.RejectionReason.ShouldNotBeNullOrEmpty();

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitLifecycleStatus,
            Arg.Any<LifecycleStatus>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_StartingToValidating_Rejected()
    {
        WithCurrentStatus(LifecycleStatus.Starting);

        var result = await _actor.TransitionAsync(LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Starting);
        result.RejectionReason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TransitionAsync_StoppingToValidating_Rejected()
    {
        WithCurrentStatus(LifecycleStatus.Stopping);

        var result = await _actor.TransitionAsync(LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Stopping);
        result.RejectionReason.ShouldNotBeNullOrEmpty();
    }
}
