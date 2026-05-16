// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Agents;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Initiative;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentActor"/>'s lifecycle state machine
/// introduced in #2364 — agents now share the same
/// <c>Draft → Validating → Stopped → Starting → Running</c> progression
/// units have. Mirrors the unit-side
/// <see cref="UnitActorValidationSchedulingTests"/> +
/// <see cref="UnitActorValidationCompletionTests"/> coverage so the
/// per-kind branches in the shared
/// <see cref="IArtefactValidationCoordinator"/> are pinned for both
/// actors.
/// </summary>
/// <remarks>
/// The key per-kind delta vs unit:
/// <list type="bullet">
///   <item>Coordinator calls route on <see cref="ArtefactKind.Agent"/>.</item>
///   <item><see cref="AgentActor.TryAutoStartAsync"/> does NOT call any
///   connector-start dispatcher — agents have no connector bindings in
///   v0.1.</item>
///   <item>The post-validation auto-start chain still drives
///   <c>Stopped → Starting → Running</c> via two successive
///   <see cref="AgentActor.TransitionAsync"/> calls.</item>
/// </list>
/// </remarks>
public class AgentActorLifecycleTests
{
    private static readonly string TestAgentActorId = TestSlugIds.HexFor("test-agent");
    private const string CurrentRunId = "run-42";

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IArtefactValidationCoordinator _coordinator = Substitute.For<IArtefactValidationCoordinator>();
    private readonly IExecutionDispatcher _dispatcher = Substitute.For<IExecutionDispatcher>();
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IUnitMembershipRepository _membershipRepository = Substitute.For<IUnitMembershipRepository>();
    private readonly IUnitPolicyEnforcer _unitPolicyEnforcer = Substitute.For<IUnitPolicyEnforcer>();
    private readonly AgentActor _actor;

    public AgentActorLifecycleTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            _loggerFactory,
            NullMessageWriterScopeFactory.Create());

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId(TestAgentActorId),
        });

        _membershipRepository
            .GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);
        _unitPolicyEnforcer.WithAllowByDefault();

        _actor = new AgentActor(
            host,
            _activityEventBus,
            Substitute.For<IAgentObservationCoordinator>(),
            new AgentMailboxCoordinator(Substitute.For<ILogger<AgentMailboxCoordinator>>()),
            new AgentDispatchCoordinator(_dispatcher, router, Substitute.For<ILogger<AgentDispatchCoordinator>>()),
            _definitionProvider,
            Array.Empty<ISkillRegistry>(),
            _membershipRepository,
            _unitPolicyEnforcer,
            Substitute.For<IAgentInitiativeEvaluator>(),
            _loggerFactory,
            Substitute.For<IAgentLifecycleCoordinator>(),
            new AgentStateCoordinator(new InMemoryAgentLiveConfigStore(), Substitute.For<ILogger<AgentStateCoordinator>>()),
            new AgentAmendmentCoordinator(Substitute.For<ILogger<AgentAmendmentCoordinator>>()),
            new AgentUnitPolicyCoordinator(Substitute.For<ILogger<AgentUnitPolicyCoordinator>>()),
            validationCoordinator: _coordinator);
        SetStateManager(_actor, _stateManager);
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

    private void WithCurrentStatus(LifecycleStatus current) =>
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.AgentLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, current));

    private void WithoutPersistedStatus() =>
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.AgentLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(false, default));

    private void WithPendingAutoStart(bool pending) =>
        _stateManager.TryGetStateAsync<bool>(StateKeys.AgentPendingAutoStart, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<bool>(pending, pending));

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

    // ──────────────────────────────────────────────────────────────────────
    // GetStatusAsync — default + persisted
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_NoPersistedState_ReturnsDraft()
    {
        WithoutPersistedStatus();

        var status = await _actor.GetStatusAsync(TestContext.Current.CancellationToken);

        status.ShouldBe(LifecycleStatus.Draft);
    }

    [Fact]
    public async Task GetStatusAsync_PersistedRunning_ReturnsRunning()
    {
        WithCurrentStatus(LifecycleStatus.Running);

        var status = await _actor.GetStatusAsync(TestContext.Current.CancellationToken);

        status.ShouldBe(LifecycleStatus.Running);
    }

    // ──────────────────────────────────────────────────────────────────────
    // TransitionAsync — happy paths, validation scheduling, rejections
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_DraftToValidating_InvokesCoordinatorWithKindAgent()
    {
        WithCurrentStatus(LifecycleStatus.Draft);
        _coordinator
            .TryStartWorkflowAsync(
                ArtefactKind.Agent,
                TestAgentActorId,
                Arg.Any<Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>>>(),
                Arg.Any<CancellationToken>())
            .Returns((TransitionResult?)null);

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Validating);

        await _coordinator.Received(1).TryStartWorkflowAsync(
            ArtefactKind.Agent,
            TestAgentActorId,
            Arg.Any<Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_StoppedToValidating_InvokesCoordinator()
    {
        WithCurrentStatus(LifecycleStatus.Stopped);
        _coordinator
            .TryStartWorkflowAsync(
                ArtefactKind.Agent,
                TestAgentActorId,
                Arg.Any<Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>>>(),
                Arg.Any<CancellationToken>())
            .Returns((TransitionResult?)null);

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        await _coordinator.Received(1).TryStartWorkflowAsync(
            ArtefactKind.Agent,
            TestAgentActorId,
            Arg.Any<Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_NonValidatingTarget_DoesNotInvokeCoordinator()
    {
        WithCurrentStatus(LifecycleStatus.Running);

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Stopping, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        await _coordinator.DidNotReceive().TryStartWorkflowAsync(
            Arg.Any<ArtefactKind>(),
            Arg.Any<string>(),
            Arg.Any<Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_DisallowedEdge_ReturnsRejection_DoesNotPersist()
    {
        // Draft → Starting is intentionally disallowed (#939) — agents must
        // pass through Validating first. Mirrors the unit-side rule.
        WithCurrentStatus(LifecycleStatus.Draft);

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Starting, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Draft);
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentLifecycleStatus, Arg.Any<LifecycleStatus>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_CoordinatorReturnsRecoveryResult_PropagatesIt()
    {
        // Scheduler-side failure (#1136) — the coordinator catches, flips
        // the actor straight to Error via the persistTransition callback,
        // and returns the recovered TransitionResult. The actor must
        // propagate that instead of the intermediate Validating result.
        WithCurrentStatus(LifecycleStatus.Draft);
        var recoveryResult = new TransitionResult(true, LifecycleStatus.Error, null);
        _coordinator
            .TryStartWorkflowAsync(
                ArtefactKind.Agent,
                TestAgentActorId,
                Arg.Any<Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>>>(),
                Arg.Any<CancellationToken>())
            .Returns(recoveryResult);

        var result = await _actor.TransitionAsync(
            LifecycleStatus.Validating, TestContext.Current.CancellationToken);

        result.ShouldBeSameAs(recoveryResult);
    }

    // ──────────────────────────────────────────────────────────────────────
    // CompleteValidationAsync — delegates to coordinator + auto-start chain
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteValidationAsync_DelegatesToCoordinatorWithKindAgent()
    {
        WithCurrentStatus(LifecycleStatus.Validating);
        _coordinator
            .CompleteValidationAsync(
                ArtefactKind.Agent,
                TestAgentActorId,
                Arg.Any<ArtefactValidationCompletion>(),
                Arg.Any<Func<CancellationToken, Task<LifecycleStatus>>>(),
                Arg.Any<Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopped, null));

        var result = await _actor.CompleteValidationAsync(
            Success(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Stopped);
        await _coordinator.Received(1).CompleteValidationAsync(
            ArtefactKind.Agent,
            TestAgentActorId,
            Arg.Any<ArtefactValidationCompletion>(),
            Arg.Any<Func<CancellationToken, Task<LifecycleStatus>>>(),
            Arg.Any<Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteValidationAsync_SuccessWithAutoStartPending_DrivesStoppedToRunning()
    {
        // After CompleteValidationAsync settles on Stopped, the actor reads
        // AgentPendingAutoStart and (if set) chains Stopped → Starting →
        // Running via two more TransitionAsync calls. Each TransitionAsync
        // reads the current status and writes the new one, so the mock
        // state needs to advance with the writes for the second transition
        // (Starting → Running) to be allowed.
        WithCurrentStatus(LifecycleStatus.Validating);
        WithPendingAutoStart(true);
        _coordinator
            .CompleteValidationAsync(
                ArtefactKind.Agent,
                TestAgentActorId,
                Arg.Any<ArtefactValidationCompletion>(),
                Arg.Any<Func<CancellationToken, Task<LifecycleStatus>>>(),
                Arg.Any<Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                WithCurrentStatus(LifecycleStatus.Stopped);
                return new TransitionResult(true, LifecycleStatus.Stopped, null);
            });
        // Each SetStateAsync writes the new status — advance the mocked
        // GetStateAsync response in lock-step so subsequent TransitionAsync
        // calls see the post-write state.
        _stateManager
            .When(s => s.SetStateAsync(StateKeys.AgentLifecycleStatus, Arg.Any<LifecycleStatus>(), Arg.Any<CancellationToken>()))
            .Do(ci => WithCurrentStatus(ci.ArgAt<LifecycleStatus>(1)));

        var result = await _actor.CompleteValidationAsync(
            Success(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();

        // Marker cleared before the chain runs.
        await _stateManager.Received(1).TryRemoveStateAsync(
            StateKeys.AgentPendingAutoStart, Arg.Any<CancellationToken>());

        // Auto-start writes Starting then Running.
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.AgentLifecycleStatus, LifecycleStatus.Starting, Arg.Any<CancellationToken>());
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.AgentLifecycleStatus, LifecycleStatus.Running, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteValidationAsync_SuccessWithoutAutoStartPending_StaysInStopped()
    {
        // /revalidate path: PendingAutoStart was never set so the actor
        // settles in Stopped — the next manual /start drives it forward.
        WithCurrentStatus(LifecycleStatus.Validating);
        WithPendingAutoStart(false);
        _coordinator
            .CompleteValidationAsync(
                ArtefactKind.Agent,
                TestAgentActorId,
                Arg.Any<ArtefactValidationCompletion>(),
                Arg.Any<Func<CancellationToken, Task<LifecycleStatus>>>(),
                Arg.Any<Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                WithCurrentStatus(LifecycleStatus.Stopped);
                return new TransitionResult(true, LifecycleStatus.Stopped, null);
            });

        var result = await _actor.CompleteValidationAsync(
            Success(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Stopped);

        // Auto-start chain MUST NOT fire.
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentLifecycleStatus, LifecycleStatus.Starting, Arg.Any<CancellationToken>());
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentLifecycleStatus, LifecycleStatus.Running, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteValidationAsync_Failure_StopsAtError_DoesNotAutoStart()
    {
        // Even when AutoStart is pending, a failure settles in Error and
        // the chain MUST NOT fire (the actor's TryAutoStartAsync only
        // fires when result.CurrentStatus == Stopped).
        WithCurrentStatus(LifecycleStatus.Validating);
        WithPendingAutoStart(true);
        _coordinator
            .CompleteValidationAsync(
                ArtefactKind.Agent,
                TestAgentActorId,
                Arg.Any<ArtefactValidationCompletion>(),
                Arg.Any<Func<CancellationToken, Task<LifecycleStatus>>>(),
                Arg.Any<Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Error, null));

        var result = await _actor.CompleteValidationAsync(
            Failure(), TestContext.Current.CancellationToken);

        result.CurrentStatus.ShouldBe(LifecycleStatus.Error);
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentLifecycleStatus, LifecycleStatus.Starting, Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // SetPendingAutoStartAsync — writes the marker, no side effects
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetPendingAutoStartAsync_PersistsTrueMarker()
    {
        await _actor.SetPendingAutoStartAsync(TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.AgentPendingAutoStart, true, Arg.Any<CancellationToken>());
    }
}
