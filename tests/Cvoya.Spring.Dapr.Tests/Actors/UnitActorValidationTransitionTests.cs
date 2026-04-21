// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

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
    private readonly IOrchestrationStrategy _strategy = Substitute.For<IOrchestrationStrategy>();
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
            _strategy,
            _activityEventBus,
            _directoryService,
            _actorProxyFactory);
        SetStateManager(_actor, _stateManager);

        // Default: no persisted status -> Draft.
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(false, default));
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

    private void WithCurrentStatus(UnitStatus current)
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, current));
    }

    // --- Allowed edges ---

    [Fact]
    public async Task TransitionAsync_DraftToValidating_Succeeds()
    {
        WithCurrentStatus(UnitStatus.Draft);

        var result = await _actor.TransitionAsync(UnitStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Validating);
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.UnitStatus,
            UnitStatus.Validating,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_ValidatingToStopped_Succeeds()
    {
        WithCurrentStatus(UnitStatus.Validating);

        var result = await _actor.TransitionAsync(UnitStatus.Stopped, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Stopped);
    }

    [Fact]
    public async Task TransitionAsync_ValidatingToError_Succeeds()
    {
        WithCurrentStatus(UnitStatus.Validating);

        var result = await _actor.TransitionAsync(UnitStatus.Error, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Error);
    }

    [Fact]
    public async Task TransitionAsync_ErrorToValidating_Succeeds()
    {
        WithCurrentStatus(UnitStatus.Error);

        var result = await _actor.TransitionAsync(UnitStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Validating);
    }

    [Fact]
    public async Task TransitionAsync_StoppedToValidating_Succeeds()
    {
        WithCurrentStatus(UnitStatus.Stopped);

        var result = await _actor.TransitionAsync(UnitStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Validating);
    }

    // --- Disallowed edges — only non-Draft, non-Stopped, non-Error states may
    // not enter Validating. Running/Starting/Stopping must first transition
    // through Stopped before requesting revalidation. ---

    [Fact]
    public async Task TransitionAsync_RunningToValidating_Rejected()
    {
        WithCurrentStatus(UnitStatus.Running);

        var result = await _actor.TransitionAsync(UnitStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(UnitStatus.Running);
        result.RejectionReason.ShouldNotBeNullOrEmpty();

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitStatus,
            Arg.Any<UnitStatus>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_StartingToValidating_Rejected()
    {
        WithCurrentStatus(UnitStatus.Starting);

        var result = await _actor.TransitionAsync(UnitStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(UnitStatus.Starting);
        result.RejectionReason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TransitionAsync_StoppingToValidating_Rejected()
    {
        WithCurrentStatus(UnitStatus.Stopping);

        var result = await _actor.TransitionAsync(UnitStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(UnitStatus.Stopping);
        result.RejectionReason.ShouldNotBeNullOrEmpty();
    }
}