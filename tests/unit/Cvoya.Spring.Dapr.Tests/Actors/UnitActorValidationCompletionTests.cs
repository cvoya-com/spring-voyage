// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

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
/// Unit tests for <see cref="UnitActor.CompleteValidationAsync"/> — the
/// terminal callback the Dapr <c>ArtefactValidationWorkflow</c> posts back to
/// the actor so it can drive <see cref="LifecycleStatus.Validating"/> →
/// <see cref="LifecycleStatus.Stopped"/> (success) or
/// <see cref="LifecycleStatus.Validating"/> → <see cref="LifecycleStatus.Error"/>
/// (failure), persist the redacted failure payload, and emit the
/// <c>StateChanged</c> activity event. Also covers the stale-run and
/// terminal-status guards that protect against superseded workflows
/// rewriting current state.
/// </summary>
public class UnitActorValidationCompletionTests
{
    private static readonly string TestUnitActorId = TestSlugIds.HexFor("test-unit");
    private const string CurrentRunId = "run-42";

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IRuntimeInvocationPath _runtimeInvocationPath = Substitute.For<IRuntimeInvocationPath>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IArtefactValidationTracker _validationTracker = Substitute.For<IArtefactValidationTracker>();
    private readonly UnitActor _actor;

    public UnitActorValidationCompletionTests()
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
            validationTracker: _validationTracker);
        SetStateManager(_actor, _stateManager);

        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Validating));
        _validationTracker
            .GetLastValidationRunIdAsync(TestUnitActorId, Arg.Any<CancellationToken>())
            .Returns(CurrentRunId);
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

    private static ArtefactValidationCompletion Success(string runId = CurrentRunId) =>
        new(true, null, runId);

    private static ArtefactValidationCompletion Failure(
        string runId = CurrentRunId,
        string code = ArtefactValidationCodes.CredentialInvalid) =>
        new(
            false,
            new ArtefactValidationError(
                ArtefactValidationStep.ValidatingCredential,
                code,
                Message: "credential rejected",
                Details: new Dictionary<string, string> { ["status"] = "401" }),
            runId);

    // --- Happy paths ---

    [Fact]
    public async Task Success_ClearsFailureBlob_TransitionsToStopped()
    {
        var result = await _actor.CompleteValidationAsync(
            Success(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Stopped);

        await _validationTracker.Received(1).SetFailureAsync(
            TestUnitActorId, null, Arg.Any<CancellationToken>());
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.UnitLifecycleStatus, LifecycleStatus.Stopped, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failure_PersistsErrorJson_TransitionsToError()
    {
        string? capturedJson = null;
        _validationTracker
            .When(t => t.SetFailureAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(ci => capturedJson = ci.ArgAt<string?>(1));

        var result = await _actor.CompleteValidationAsync(
            Failure(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Error);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.UnitLifecycleStatus, LifecycleStatus.Error, Arg.Any<CancellationToken>());

        capturedJson.ShouldNotBeNull();
        // Round-trip the JSON through System.Text.Json to confirm the
        // failure payload is serialized correctly (no Newtonsoft in scope).
        var roundTripped = JsonSerializer.Deserialize<ArtefactValidationError>(capturedJson!);
        roundTripped!.Step.ShouldBe(ArtefactValidationStep.ValidatingCredential);
        roundTripped.Code.ShouldBe(ArtefactValidationCodes.CredentialInvalid);
        roundTripped.Message.ShouldBe("credential rejected");
        roundTripped.Details!["status"].ShouldBe("401");
    }

    [Fact]
    public async Task Failure_EmitsWarningStateChangedActivityWithValidationContext()
    {
        // #1665: the StateChanged row used to be tagged Debug with a bare
        // "Unit transitioned from Validating to Error" summary — invisible in
        // the Activity tab and devoid of any cue as to *why*. Assert that the
        // failure path now emits a Warning row with the validation code +
        // message embedded in summary and details.
        ActivityEvent? capturedEvent = null;
        _activityEventBus
            .When(b => b.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>()))
            .Do(ci => capturedEvent = ci.ArgAt<ActivityEvent>(0));

        await _actor.CompleteValidationAsync(
            Failure(), TestContext.Current.CancellationToken);

        capturedEvent.ShouldNotBeNull();
        capturedEvent!.EventType.ShouldBe(ActivityEventType.StateChanged);
        capturedEvent.Severity.ShouldBe(ActivitySeverity.Warning);
        capturedEvent.Summary.ShouldContain(ArtefactValidationCodes.CredentialInvalid);
        capturedEvent.Summary.ShouldContain("credential rejected");

        capturedEvent.Details.ShouldNotBeNull();
        var details = capturedEvent.Details!.Value;
        details.GetProperty("action").GetString().ShouldBe("StatusTransition");
        details.GetProperty("from").GetString().ShouldBe(LifecycleStatus.Validating.ToString());
        details.GetProperty("to").GetString().ShouldBe(LifecycleStatus.Error.ToString());
        details.GetProperty("validationCode").GetString().ShouldBe(ArtefactValidationCodes.CredentialInvalid);
        details.GetProperty("validationMessage").GetString().ShouldBe("credential rejected");
        details.GetProperty("validationStep").GetString().ShouldBe(ArtefactValidationStep.ValidatingCredential.ToString());
        // The full structured error blob is also present so the portal can
        // expand the row to show every field (including validation Details).
        details.GetProperty("error").GetProperty("Code").GetString()
            .ShouldBe(ArtefactValidationCodes.CredentialInvalid);
    }

    [Fact]
    public async Task Success_EmitsDebugStateChangedActivityWithoutValidationContext()
    {
        // Negative control for the test above — non-failure transitions
        // keep the original Debug severity and bare details payload so we
        // don't accidentally promote every StateChanged row to Warning.
        ActivityEvent? capturedEvent = null;
        _activityEventBus
            .When(b => b.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>()))
            .Do(ci => capturedEvent = ci.ArgAt<ActivityEvent>(0));

        await _actor.CompleteValidationAsync(
            Success(), TestContext.Current.CancellationToken);

        capturedEvent.ShouldNotBeNull();
        capturedEvent!.EventType.ShouldBe(ActivityEventType.StateChanged);
        capturedEvent.Severity.ShouldBe(ActivitySeverity.Debug);
        capturedEvent.Summary.ShouldBe(
            $"Unit transitioned from {LifecycleStatus.Validating} to {LifecycleStatus.Stopped}");

        capturedEvent.Details.ShouldNotBeNull();
        var details = capturedEvent.Details!.Value;
        details.TryGetProperty("validationCode", out _).ShouldBeFalse();
        details.TryGetProperty("error", out _).ShouldBeFalse();
    }

    // --- Guards ---

    [Fact]
    public async Task StaleRun_NoOp_NoTransition_NoWrite()
    {
        _validationTracker
            .GetLastValidationRunIdAsync(TestUnitActorId, Arg.Any<CancellationToken>())
            .Returns("run-99"); // current differs from completion's WorkflowInstanceId

        var result = await _actor.CompleteValidationAsync(
            Success(runId: "run-stale"), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Validating);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitLifecycleStatus, Arg.Any<LifecycleStatus>(), Arg.Any<CancellationToken>());
        await _validationTracker.DidNotReceive().SetFailureAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TerminalStatusStopped_NoOp_NoWrite()
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Stopped));

        var result = await _actor.CompleteValidationAsync(
            Failure(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Stopped);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitLifecycleStatus, Arg.Any<LifecycleStatus>(), Arg.Any<CancellationToken>());
        await _validationTracker.DidNotReceive().SetFailureAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TerminalStatusError_NoOp_NoWrite()
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Error));

        var result = await _actor.CompleteValidationAsync(
            Success(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Error);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitLifecycleStatus, Arg.Any<LifecycleStatus>(), Arg.Any<CancellationToken>());
    }

    // --- Round-trip safety ---

    [Fact]
    public void ArtefactValidationError_RoundTripsThroughSystemTextJson()
    {
        // Defensive: if System.Text.Json can't round-trip the failure shape
        // (e.g. the Details dictionary), CompleteValidationAsync's persistence
        // path would silently truncate. Exercise the same serializer the
        // actor uses. Note: the default System.Text.Json serialization used
        // for the persisted blob writes enums as their ordinal — the
        // API-layer response converts to a string via JsonStringEnumConverter
        // configured in Program.cs, so operator-facing output reads
        // "ResolvingModel" even though the on-disk JSON holds 3.
        var error = new ArtefactValidationError(
            ArtefactValidationStep.ResolvingModel,
            ArtefactValidationCodes.ModelNotFound,
            Message: "model foo not found",
            Details: new Dictionary<string, string>
            {
                ["model"] = "foo",
                ["http_status"] = "404",
            });

        var json = JsonSerializer.Serialize(error);
        json.ShouldContain("ModelNotFound");

        var restored = JsonSerializer.Deserialize<ArtefactValidationError>(json);
        restored.ShouldNotBeNull();
        restored!.Step.ShouldBe(ArtefactValidationStep.ResolvingModel);
        restored.Code.ShouldBe(ArtefactValidationCodes.ModelNotFound);
        restored.Message.ShouldBe("model foo not found");
        restored.Details!["model"].ShouldBe("foo");
        restored.Details!["http_status"].ShouldBe("404");
    }
}
