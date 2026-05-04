// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Reflection;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Initiative;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the Tier-2 reflection-action dispatch path introduced by #100
/// and extended by #552 (evaluator wiring). Verifies that
/// <see cref="AgentActor.ReceiveReminderAsync"/>:
/// <list type="bullet">
///   <item><description>translates the outcome through the
///     <see cref="IReflectionActionHandlerRegistry"/>;</description></item>
///   <item><description>applies the cross-cutting
///     <see cref="IUnitPolicyEnforcer.EvaluateSkillInvocationAsync"/> gate;</description></item>
///   <item><description>consults the <see cref="IAgentInitiativeEvaluator"/>
///     seam (the #552 decision point) and maps its three-valued decision
///     onto dispatch / proposal / defer semantics;</description></item>
///   <item><description>routes through <see cref="MessageRouter"/> only when
///     the evaluator returned <c>ActAutonomously</c>.</description></item>
/// </list>
/// </summary>
public class AgentActorReflectionDispatchTests
{
    private static readonly Guid AgentId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly string AgentIdHex = AgentId.ToString("N");

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IInitiativeEngine _initiativeEngine = Substitute.For<IInitiativeEngine>();
    private readonly IExecutionDispatcher _dispatcher = Substitute.For<IExecutionDispatcher>();
    private readonly MessageRouter _router;
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IUnitMembershipRepository _membershipRepository = Substitute.For<IUnitMembershipRepository>();
    private readonly IReflectionActionHandlerRegistry _registry = Substitute.For<IReflectionActionHandlerRegistry>();
    private readonly IUnitPolicyEnforcer _unitPolicyEnforcer = Substitute.For<IUnitPolicyEnforcer>();
    private readonly IAgentInitiativeEvaluator _initiativeEvaluator = Substitute.For<IAgentInitiativeEvaluator>();
    private readonly AgentActor _actor;

    public AgentActorReflectionDispatchTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory);
        _router.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Success(null));

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId(AgentIdHex),
        });

        _membershipRepository
            .GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        _unitPolicyEnforcer.WithAllowByDefault();

        // Default: evaluator green-lights the action autonomously so tests
        // that don't care about the initiative seam can progress straight
        // through dispatch. Individual tests override this to exercise
        // Defer / ActWithConfirmation.
        _initiativeEvaluator.WithActAutonomouslyByDefault();

        // Wire the real AgentObservationCoordinator with the mocked seams so
        // initiative-dispatch tests exercise the coordinator's logic end-to-end
        // without going through a full actor stack. The scoped seams
        // (IUnitPolicyEnforcer, IAgentInitiativeEvaluator) are injected into
        // AgentActor (which owns the scoped lifetime) and passed to the
        // coordinator as delegates on each reminder tick.
        var observationCoordinator = new AgentObservationCoordinator(
            _initiativeEngine,
            _registry,
            _router,
            _definitionProvider,
            Substitute.For<ILogger<AgentObservationCoordinator>>());

        _actor = new AgentActor(
            host,
            _activityEventBus,
            observationCoordinator,
            new AgentMailboxCoordinator(Substitute.For<ILogger<AgentMailboxCoordinator>>()),
            new AgentDispatchCoordinator(_dispatcher, _router, Substitute.For<ILogger<AgentDispatchCoordinator>>()),
            _definitionProvider,
            Array.Empty<ISkillRegistry>(),
            _membershipRepository,
            _unitPolicyEnforcer,
            _initiativeEvaluator,
            loggerFactory,
            Substitute.For<IAgentLifecycleCoordinator>(),
            new AgentStateCoordinator(Substitute.For<ILogger<AgentStateCoordinator>>()),
            new AgentAmendmentCoordinator(Substitute.For<ILogger<AgentAmendmentCoordinator>>()),
            new AgentUnitPolicyCoordinator(Substitute.For<ILogger<AgentUnitPolicyCoordinator>>()));

        SetStateManager(_actor, _stateManager);

        // Default: observation channel has a single observation so RunInitiativeCheck will progress.
        _stateManager.TryGetStateAsync<List<JsonElement>>(StateKeys.ObservationChannel, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<JsonElement>>(true,
                [JsonSerializer.SerializeToElement(new { summary = "note" })]));
    }

    private static void SetStateManager(Actor actor, IActorStateManager stateManager)
    {
        var field = typeof(Actor).GetField(
            "<StateManager>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
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

    private void ArrangeOutcome(ReflectionOutcome outcome)
    {
        _initiativeEngine
            .ProcessObservationsAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<JsonElement>>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(outcome);
    }

    private void ArrangeHandler(string actionType, Message? translated)
    {
        var handler = Substitute.For<IReflectionActionHandler>();
        handler.ActionType.Returns(actionType);
        handler
            .TranslateAsync(Arg.Any<Address>(), Arg.Any<ReflectionOutcome>(), Arg.Any<CancellationToken>())
            .Returns(translated);
        _registry.Find(actionType).Returns(handler);
    }

    private static readonly Guid TargetAgentId = new("aaaaaaaa-0000-0000-0000-000000000002");

    private static Message TranslatedMessage(Guid? target = null)
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", AgentId),
            new Address("agent", target ?? TargetAgentId),
            MessageType.Domain,
            "conv-reflection",
            JsonSerializer.SerializeToElement(new { Content = "hi" }),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Reflection_EvaluatorActsAutonomously_RoutesTranslatedMessage()
    {
        var outcome = new ReflectionOutcome(true, "send-message", "because",
            JsonSerializer.SerializeToElement(new { }));
        ArrangeOutcome(outcome);
        var translated = TranslatedMessage();
        ArrangeHandler("send-message", translated);

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _router.Received(1).RouteAsync(
            Arg.Is<Message>(m => m.Id == translated.Id),
            Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.ReflectionActionDispatched),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_EvaluatorConsultedWithObservationSignals()
    {
        // The #552 wiring passes the drained observation batch as Signals on
        // the evaluator's context so a Proactive / Autonomous evaluator can
        // use them to decide. Verify the signal batch reaches the seam.
        var outcome = new ReflectionOutcome(true, "send-message", "because",
            JsonSerializer.SerializeToElement(new { }));
        ArrangeOutcome(outcome);
        ArrangeHandler("send-message", TranslatedMessage());

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _initiativeEvaluator.Received(1).EvaluateAsync(
            Arg.Is<InitiativeEvaluationContext>(c =>
                c.AgentId == AgentIdHex &&
                c.Action.ActionType == "send-message" &&
                c.Signals.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_EvaluatorReturnsDefer_NoDispatchNoActivityEvent()
    {
        // #552 acceptance: Defer takes no action and emits no activity event.
        _initiativeEvaluator
            .EvaluateAsync(Arg.Any<InitiativeEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(InitiativeEvaluationResult.Defer(
                InitiativeLevel.Passive,
                "agent initiative level is below Proactive"));

        var outcome = new ReflectionOutcome(true, "send-message", "because",
            JsonSerializer.SerializeToElement(new { }));
        ArrangeOutcome(outcome);
        ArrangeHandler("send-message", TranslatedMessage());

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _router.DidNotReceive().RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ReflectionActionDispatched ||
                e.EventType == ActivityEventType.ReflectionActionProposed ||
                e.EventType == ActivityEventType.ReflectionActionSkipped),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_EvaluatorReturnsActWithConfirmation_EmitsProposalDoesNotRoute()
    {
        // #552 acceptance: Proactive surfaces a proposal for the appropriate
        // owner and emits an activity entry — but does not dispatch inline.
        _initiativeEvaluator
            .EvaluateAsync(Arg.Any<InitiativeEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(InitiativeEvaluationResult.WithConfirmation(
                InitiativeLevel.Proactive,
                "proactive level always requires confirmation"));

        var outcome = new ReflectionOutcome(true, "send-message", "because",
            JsonSerializer.SerializeToElement(new { }));
        ArrangeOutcome(outcome);
        ArrangeHandler("send-message", TranslatedMessage());

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _router.DidNotReceive().RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ReflectionActionProposed &&
                e.Summary.Contains("proactive level always requires confirmation")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_EvaluatorFailClosedDowngrade_ProposalDetailsCarryFlag()
    {
        // #552: a fail-closed downgrade is a distinct operator signal — the
        // proposal activity entry must carry the flag so dashboards can
        // surface the degraded posture separately from "operator asked for
        // confirmation."
        _initiativeEvaluator
            .EvaluateAsync(Arg.Any<InitiativeEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(InitiativeEvaluationResult.WithConfirmation(
                InitiativeLevel.Autonomous,
                "cost gate unresolved",
                failedClosed: true));

        var outcome = new ReflectionOutcome(true, "send-message", "because",
            JsonSerializer.SerializeToElement(new { }));
        ArrangeOutcome(outcome);
        ArrangeHandler("send-message", TranslatedMessage());

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ReflectionActionProposed &&
                e.Summary.Contains("fail-closed")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_EvaluatorThrows_EmitsFailClosedProposal()
    {
        // #552: an infrastructure failure in the evaluator itself must not
        // escape the actor turn AND must not silently authorise dispatch —
        // surface a fail-closed proposal so an operator can triage.
        _initiativeEvaluator
            .EvaluateAsync(Arg.Any<InitiativeEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns<InitiativeEvaluationResult>(_ => throw new InvalidOperationException("boom"));

        var outcome = new ReflectionOutcome(true, "send-message", "because",
            JsonSerializer.SerializeToElement(new { }));
        ArrangeOutcome(outcome);
        ArrangeHandler("send-message", TranslatedMessage());

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _router.DidNotReceive().RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ReflectionActionProposed &&
                e.Summary.Contains("fail-closed")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_UnknownActionType_EmitsSkippedAndDoesNotRoute()
    {
        var outcome = new ReflectionOutcome(true, "totally-made-up", "because",
            JsonSerializer.SerializeToElement(new { }));
        ArrangeOutcome(outcome);
        _registry.Find("totally-made-up").Returns((IReflectionActionHandler?)null);

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _router.DidNotReceive().RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.ReflectionActionSkipped),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_ActionBlockedByUnitSkillPolicy_EmitsSkipped()
    {
        // Skill-policy is cross-cutting (not initiative-specific) — the
        // #552 evaluator wiring intentionally leaves this gate on the
        // dispatch path, so a blocked skill is still reported as
        // ReflectionActionSkipped (not a proposal).
        _unitPolicyEnforcer
            .EvaluateSkillInvocationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Deny("Tool 'send-message' blocked", "engineering"));

        var outcome = new ReflectionOutcome(true, "send-message", "because",
            JsonSerializer.SerializeToElement(new { }));
        ArrangeOutcome(outcome);
        ArrangeHandler("send-message", TranslatedMessage());

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _router.DidNotReceive().RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.ReflectionActionSkipped),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_UnitSkillPolicyBlocks_EvaluatorNotConsulted()
    {
        // The cross-cutting skill gate runs before the evaluator — the
        // evaluator should not be invoked at all for a skill denial so the
        // cost of composing the initiative gates is skipped on the hot
        // path.
        _unitPolicyEnforcer
            .EvaluateSkillInvocationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Deny("Tool 'send-message' blocked", "engineering"));

        var outcome = new ReflectionOutcome(true, "send-message", "because",
            JsonSerializer.SerializeToElement(new { }));
        ArrangeOutcome(outcome);
        ArrangeHandler("send-message", TranslatedMessage());

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _initiativeEvaluator.DidNotReceive().EvaluateAsync(
            Arg.Any<InitiativeEvaluationContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_ShouldActFalse_NoDispatchOrSkipEvent()
    {
        var outcome = new ReflectionOutcome(false);
        ArrangeOutcome(outcome);

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _router.DidNotReceive().RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.ReflectionActionDispatched),
            Arg.Any<CancellationToken>());
        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.ReflectionActionSkipped),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_HandlerReturnsNull_EmitsSkippedMalformedPayload()
    {
        var outcome = new ReflectionOutcome(true, "send-message", "because",
            JsonSerializer.SerializeToElement(new { }));
        ArrangeOutcome(outcome);
        ArrangeHandler("send-message", translated: null);

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _router.DidNotReceive().RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.ReflectionActionSkipped),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_PlumbsAgentInstructionsFromDefinitionToEngine()
    {
        // #1617 acceptance: when the actor's reminder fires, the observation
        // coordinator must resolve the agent's real instructions from
        // IAgentDefinitionProvider and pass them to InitiativeEngine — not a
        // synthesised stand-in.
        const string instructions = "You are a release-engineering agent. Triage build failures.";
        _definitionProvider
            .GetByIdAsync(AgentIdHex, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: AgentIdHex,
                Name: "Release Engineer",
                Instructions: instructions,
                Execution: null));

        var outcome = new ReflectionOutcome(false);
        ArrangeOutcome(outcome);

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _initiativeEngine.Received(1).ProcessObservationsAsync(
            AgentIdHex,
            Arg.Any<IReadOnlyList<JsonElement>>(),
            instructions,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_NoAgentDefinition_PassesNullInstructionsToEngine()
    {
        // When the definition provider returns null (agent has no
        // configured definition row), the coordinator should still call the
        // engine — passing null lets the engine substitute its documented
        // fallback rather than the call site inventing a stand-in.
        _definitionProvider
            .GetByIdAsync(AgentIdHex, Arg.Any<CancellationToken>())
            .Returns((AgentDefinition?)null);

        var outcome = new ReflectionOutcome(false);
        ArrangeOutcome(outcome);

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _initiativeEngine.Received(1).ProcessObservationsAsync(
            AgentIdHex,
            Arg.Any<IReadOnlyList<JsonElement>>(),
            (string?)null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_DefinitionProviderThrows_StillCallsEngineWithNullInstructions()
    {
        // A lookup failure in IAgentDefinitionProvider must not stop the
        // initiative tick: the engine is still invoked with null
        // instructions so it can apply its missing-instructions fallback.
        _definitionProvider
            .GetByIdAsync(AgentIdHex, Arg.Any<CancellationToken>())
            .Returns<AgentDefinition?>(_ => throw new InvalidOperationException("db down"));

        var outcome = new ReflectionOutcome(false);
        ArrangeOutcome(outcome);

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _initiativeEngine.Received(1).ProcessObservationsAsync(
            AgentIdHex,
            Arg.Any<IReadOnlyList<JsonElement>>(),
            (string?)null,
            Arg.Any<CancellationToken>());
    }
}