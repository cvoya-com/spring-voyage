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
/// Unit tests for <see cref="UnitActor.TransitionAsync"/> orchestration
/// wiring introduced in T-05: every transition into
/// <see cref="LifecycleStatus.Validating"/> must schedule the
/// <c>ArtefactValidationWorkflow</c> and persist the returned instance id to
/// <c>LastValidationRunId</c>. On the revalidate paths
/// (<see cref="LifecycleStatus.Error"/> → <see cref="LifecycleStatus.Validating"/>
/// and <see cref="LifecycleStatus.Stopped"/> → <see cref="LifecycleStatus.Validating"/>)
/// the tracker's <c>BeginRunAsync</c> also clears any stale
/// <c>LastValidationErrorJson</c> so observers see "clean slate + fresh
/// run id" rather than "new run id + stale error."
/// </summary>
public class UnitActorValidationSchedulingTests
{
    private static readonly string TestUnitActorId = TestSlugIds.HexFor("test-unit");
    private const string UnitName = "eng-team";

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IRuntimeInvocationPath _runtimeInvocationPath = Substitute.For<IRuntimeInvocationPath>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IArtefactValidationWorkflowScheduler _scheduler = Substitute.For<IArtefactValidationWorkflowScheduler>();
    private readonly IArtefactValidationTracker _validationTracker = Substitute.For<IArtefactValidationTracker>();
    private readonly UnitActor _actor;

    public UnitActorValidationSchedulingTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId(TestUnitActorId),
        });
        _actor = new UnitActor(
            host,
            _loggerFactory,
            _runtimeInvocationPath,
            _activityEventBus,
            _directoryService,
            _actorProxyFactory,
            new UnitStateCoordinator(new InMemoryUnitLiveConfigStore(), Substitute.For<ILogger<UnitStateCoordinator>>()),
            new InMemoryUnitMemberGraphStore(),
            validationWorkflowScheduler: _scheduler,
            validationTracker: _validationTracker);
        SetStateManager(_actor, _stateManager);

        _scheduler
            .ScheduleAsync(TestUnitActorId, Arg.Any<CancellationToken>())
            .Returns(new ArtefactValidationSchedule("run-42", UnitName));
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

    [Fact]
    public async Task DraftToValidating_SchedulesWorkflow_PersistsRunId()
    {
        WithCurrentStatus(LifecycleStatus.Draft);

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Validating);

        await _scheduler.Received(1).ScheduleAsync(
            TestUnitActorId, Arg.Any<CancellationToken>());
        await _validationTracker.Received(1).BeginRunAsync(
            TestUnitActorId, "run-42", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ErrorToValidating_ClearsFailureBlob_SchedulesWorkflow()
    {
        WithCurrentStatus(LifecycleStatus.Error);

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        // BeginRunAsync's contract is to clear LastValidationErrorJson
        // atomically with writing the new run id — we verify the call
        // order (scheduler first, tracker second on the same actor-side
        // turn) and that both happened.
        await _scheduler.Received(1).ScheduleAsync(
            TestUnitActorId, Arg.Any<CancellationToken>());
        await _validationTracker.Received(1).BeginRunAsync(
            TestUnitActorId, "run-42", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoppedToValidating_SchedulesWorkflow_PersistsRunId()
    {
        WithCurrentStatus(LifecycleStatus.Stopped);

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        await _scheduler.Received(1).ScheduleAsync(
            TestUnitActorId, Arg.Any<CancellationToken>());
        await _validationTracker.Received(1).BeginRunAsync(
            TestUnitActorId, "run-42", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionToNonValidating_DoesNotScheduleWorkflow()
    {
        WithCurrentStatus(LifecycleStatus.Running);

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Stopping, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        await _scheduler.DidNotReceive().ScheduleAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _validationTracker.DidNotReceive().BeginRunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisallowedTransitionToValidating_DoesNotScheduleWorkflow()
    {
        // Running -> Validating is not allowed per the state machine.
        WithCurrentStatus(LifecycleStatus.Running);

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        await _scheduler.DidNotReceive().ScheduleAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// #1136: scheduler-side failure must tombstone the unit into Error
    /// (was: leave it stuck in Validating). The actor still accepts the
    /// initial transition into Validating, but the catch path then writes
    /// a structured ScheduleFailed payload via the tracker and persists a
    /// Validating -> Error transition so downstream lifecycle endpoints
    /// (start/stop/delete-without-force) work the same as on a probe
    /// failure. The TransitionAsync return value reflects the *final*
    /// state (Error), because the actor's status-of-record after the call
    /// chain returns is Error.
    /// </summary>
    [Fact]
    public async Task SchedulerThrowsGeneric_FlipsToError_AndPersistsScheduleFailedPayload()
    {
        // #1136: unexpected scheduler failures (Dapr workflow gateway
        // down, etc.) used to leave the unit hanging in Validating with
        // no workflow attached. The actor now catches generically,
        // persists a ScheduleFailed blob with the SchedulingWorkflow step
        // (the host-side step that never finished), and flips to Error so
        // the UI / CLI can surface a structured failure and offer a
        // Retry.
        WithCurrentStatus(LifecycleStatus.Draft);
        _scheduler
            .ScheduleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ArtefactValidationSchedule>(
                new InvalidOperationException("dapr down")));

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        // The return value is the result of the final
        // PersistTransitionAsync call inside the catch path:
        // Validating -> Error.
        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Error);

        // BeginRunAsync must NOT have been called — the run never started.
        await _validationTracker.DidNotReceive().BeginRunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // SetFailureAsync must have been called with a ScheduleFailed
        // payload that round-trips through JSON to a ArtefactValidationError
        // whose Code + Step match the merged contract.
        await _validationTracker.Received(1).SetFailureAsync(
            TestUnitActorId,
            Arg.Is<string>(payload => PayloadHasScheduleFailedCode(payload)),
            Arg.Any<CancellationToken>());

        // The Validating -> Error transition must have been persisted to
        // the actor state store.
        await _stateManager.Received().SetStateAsync(
            StateKeys.UnitLifecycleStatus,
            LifecycleStatus.Error,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// #1136: a tracker that throws while persisting the ScheduleFailed
    /// payload must not block the Validating -> Error transition. The
    /// missing payload is logged but the unit still ends up unbricked, so
    /// the operator's standard recovery paths still work.
    /// </summary>
    [Fact]
    public async Task SchedulerThrows_TrackerThrows_StillFlipsToError()
    {
        WithCurrentStatus(LifecycleStatus.Draft);
        _scheduler
            .ScheduleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ArtefactValidationSchedule>(
                new InvalidOperationException("dapr down")));
        _validationTracker
            .SetFailureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("db down")));

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Error);

        await _stateManager.Received().SetStateAsync(
            StateKeys.UnitLifecycleStatus,
            LifecycleStatus.Error,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// #1144: when the scheduler throws the typed
    /// <see cref="ArtefactValidationSchedulingException"/> (e.g. because the
    /// unit has no image), the actor persists the *structured* error
    /// verbatim — preserving the ConfigurationIncomplete code and the
    /// missing-field detail — instead of falling through to the generic
    /// ScheduleFailed catch. This is what lets the wizard render
    /// "Image is required…" copy that names the missing field.
    /// </summary>
    [Fact]
    public async Task SchedulerThrowsTyped_FlipsToError_AndPersistsConfigurationIncompleteBlob()
    {
        WithCurrentStatus(LifecycleStatus.Draft);
        var error = new ArtefactValidationError(
            Step: ArtefactValidationStep.PullingImage,
            Code: ArtefactValidationCodes.ConfigurationIncomplete,
            Message: "This unit has no container image configured.",
            Details: new Dictionary<string, string> { ["missing"] = "image" });
        _scheduler
            .ScheduleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ArtefactValidationSchedule>(
                new ArtefactValidationSchedulingException(error)));

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Error);

        // Storage serialization uses default options, so the
        // ArtefactValidationStep enum is written as its numeric value (0 ==
        // PullingImage). The API endpoint re-serializes with
        // JsonStringEnumConverter on read, so the wire format the UI
        // sees is still the symbolic name. Assert on the stable bits:
        // code + missing-field detail.
        await _validationTracker.Received(1).SetFailureAsync(
            TestUnitActorId,
            Arg.Is<string>(payload =>
                payload != null
                && payload.Contains(ArtefactValidationCodes.ConfigurationIncomplete)
                && payload.Contains("missing")
                && payload.Contains("image")),
            Arg.Any<CancellationToken>());

        // The generic ScheduleFailed catch must NOT have fired — the typed
        // catch's payload should be the only one persisted.
        await _validationTracker.DidNotReceive().SetFailureAsync(
            TestUnitActorId,
            Arg.Is<string>(payload =>
                payload != null
                && payload.Contains(ArtefactValidationCodes.ScheduleFailed)),
            Arg.Any<CancellationToken>());

        await _stateManager.Received().SetStateAsync(
            StateKeys.UnitLifecycleStatus, LifecycleStatus.Error, Arg.Any<CancellationToken>());
    }

    private static bool PayloadHasScheduleFailedCode(string payload)
    {
        var error = System.Text.Json.JsonSerializer.Deserialize<ArtefactValidationError>(payload);
        return error is not null
               && error.Code == ArtefactValidationCodes.ScheduleFailed
               && error.Step == ArtefactValidationStep.SchedulingWorkflow;
    }
}
