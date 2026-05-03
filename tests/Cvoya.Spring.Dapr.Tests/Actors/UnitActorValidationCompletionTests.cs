// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

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
/// Unit tests for <see cref="UnitActor.CompleteValidationAsync"/> — the
/// terminal callback the Dapr <c>UnitValidationWorkflow</c> posts back to
/// the actor so it can drive <see cref="UnitStatus.Validating"/> →
/// <see cref="UnitStatus.Stopped"/> (success) or
/// <see cref="UnitStatus.Validating"/> → <see cref="UnitStatus.Error"/>
/// (failure), persist the redacted failure payload, and emit the
/// <c>StateChanged</c> activity event. Also covers the stale-run and
/// terminal-status guards that protect against superseded workflows
/// rewriting current state.
/// </summary>
public class UnitActorValidationCompletionTests
{
    private const string TestUnitActorId = "test-unit";
    private const string CurrentRunId = "run-42";

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IOrchestrationStrategy _strategy = Substitute.For<IOrchestrationStrategy>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IUnitValidationTracker _validationTracker = Substitute.For<IUnitValidationTracker>();
    private readonly UnitActor _actor;

    public UnitActorValidationCompletionTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
        });
        _actor = new UnitActor(
            host,
            _loggerFactory,
            _strategy,
            _activityEventBus,
            _directoryService,
            _actorProxyFactory,
            validationTracker: _validationTracker);
        SetStateManager(_actor, _stateManager);

        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Validating));
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

    private static UnitValidationCompletion Success(string runId = CurrentRunId) =>
        new(true, null, runId);

    private static UnitValidationCompletion Failure(
        string runId = CurrentRunId,
        string code = UnitValidationCodes.CredentialInvalid) =>
        new(
            false,
            new UnitValidationError(
                UnitValidationStep.ValidatingCredential,
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
        result.CurrentStatus.ShouldBe(UnitStatus.Stopped);

        await _validationTracker.Received(1).SetFailureAsync(
            TestUnitActorId, null, Arg.Any<CancellationToken>());
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.UnitStatus, UnitStatus.Stopped, Arg.Any<CancellationToken>());
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
        result.CurrentStatus.ShouldBe(UnitStatus.Error);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.UnitStatus, UnitStatus.Error, Arg.Any<CancellationToken>());

        capturedJson.ShouldNotBeNull();
        // Round-trip the JSON through System.Text.Json to confirm the
        // failure payload is serialized correctly (no Newtonsoft in scope).
        var roundTripped = JsonSerializer.Deserialize<UnitValidationError>(capturedJson!);
        roundTripped!.Step.ShouldBe(UnitValidationStep.ValidatingCredential);
        roundTripped.Code.ShouldBe(UnitValidationCodes.CredentialInvalid);
        roundTripped.Message.ShouldBe("credential rejected");
        roundTripped.Details!["status"].ShouldBe("401");
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
        result.CurrentStatus.ShouldBe(UnitStatus.Validating);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitStatus, Arg.Any<UnitStatus>(), Arg.Any<CancellationToken>());
        await _validationTracker.DidNotReceive().SetFailureAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TerminalStatusStopped_NoOp_NoWrite()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Stopped));

        var result = await _actor.CompleteValidationAsync(
            Failure(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(UnitStatus.Stopped);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitStatus, Arg.Any<UnitStatus>(), Arg.Any<CancellationToken>());
        await _validationTracker.DidNotReceive().SetFailureAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TerminalStatusError_NoOp_NoWrite()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Error));

        var result = await _actor.CompleteValidationAsync(
            Success(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(UnitStatus.Error);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitStatus, Arg.Any<UnitStatus>(), Arg.Any<CancellationToken>());
    }

    // --- Round-trip safety ---

    [Fact]
    public void UnitValidationError_RoundTripsThroughSystemTextJson()
    {
        // Defensive: if System.Text.Json can't round-trip the failure shape
        // (e.g. the Details dictionary), CompleteValidationAsync's persistence
        // path would silently truncate. Exercise the same serializer the
        // actor uses. Note: the default System.Text.Json serialization used
        // for the persisted blob writes enums as their ordinal — the
        // API-layer response converts to a string via JsonStringEnumConverter
        // configured in Program.cs, so operator-facing output reads
        // "ResolvingModel" even though the on-disk JSON holds 3.
        var error = new UnitValidationError(
            UnitValidationStep.ResolvingModel,
            UnitValidationCodes.ModelNotFound,
            Message: "model foo not found",
            Details: new Dictionary<string, string>
            {
                ["model"] = "foo",
                ["http_status"] = "404",
            });

        var json = JsonSerializer.Serialize(error);
        json.ShouldContain("ModelNotFound");

        var restored = JsonSerializer.Deserialize<UnitValidationError>(json);
        restored.ShouldNotBeNull();
        restored!.Step.ShouldBe(UnitValidationStep.ResolvingModel);
        restored.Code.ShouldBe(UnitValidationCodes.ModelNotFound);
        restored.Message.ShouldBe("model foo not found");
        restored.Details!["model"].ShouldBe("foo");
        restored.Details!["http_status"].ShouldBe("404");
    }
}