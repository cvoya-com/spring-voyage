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
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the Tier-2 reflection-action dispatch path introduced by #100.
/// Verifies that <see cref="AgentActor.ReceiveReminderAsync"/> translates a
/// <see cref="ReflectionOutcome"/> into a <see cref="Message"/> via the
/// <see cref="IReflectionActionHandlerRegistry"/>, gates it through the
/// agent's <see cref="InitiativePolicy"/> and the unit-level
/// <see cref="IUnitPolicyEnforcer"/>, and routes it through
/// <see cref="MessageRouter"/>.
/// </summary>
public class AgentActorReflectionDispatchTests
{
    private const string AgentId = "ada";

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IInitiativeEngine _initiativeEngine = Substitute.For<IInitiativeEngine>();
    private readonly IAgentPolicyStore _policyStore = Substitute.For<IAgentPolicyStore>();
    private readonly IExecutionDispatcher _dispatcher = Substitute.For<IExecutionDispatcher>();
    private readonly MessageRouter _router;
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IUnitMembershipRepository _membershipRepository = Substitute.For<IUnitMembershipRepository>();
    private readonly IReflectionActionHandlerRegistry _registry = Substitute.For<IReflectionActionHandlerRegistry>();
    private readonly IUnitPolicyEnforcer _unitPolicyEnforcer = Substitute.For<IUnitPolicyEnforcer>();
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
            ActorId = new ActorId(AgentId),
        });

        _membershipRepository
            .GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        _unitPolicyEnforcer.WithAllowByDefault();

        _policyStore.GetPolicyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new InitiativePolicy(MaxLevel: InitiativeLevel.Autonomous));

        _actor = new AgentActor(
            host,
            _activityEventBus,
            _initiativeEngine,
            _policyStore,
            _dispatcher,
            _router,
            _definitionProvider,
            Array.Empty<ISkillRegistry>(),
            _membershipRepository,
            _registry,
            _unitPolicyEnforcer,
            loggerFactory);

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
            .ProcessObservationsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<JsonElement>>(), Arg.Any<CancellationToken>())
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

    private static Message TranslatedMessage(string target = "bob")
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", AgentId),
            new Address("agent", target),
            MessageType.Domain,
            "conv-reflection",
            JsonSerializer.SerializeToElement(new { Content = "hi" }),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Reflection_ShouldActValidAction_RoutesTranslatedMessage()
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
    public async Task Reflection_ActionBlockedByAgentPolicy_EmitsSkipped()
    {
        _policyStore.GetPolicyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new InitiativePolicy(
                MaxLevel: InitiativeLevel.Autonomous,
                BlockedActions: new[] { "send-message" }));

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
    public async Task Reflection_ActionBlockedByUnitSkillPolicy_EmitsSkipped()
    {
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
    public async Task Reflection_ActionBlockedByUnitInitiativePolicy_EmitsSkipped()
    {
        // #250 — even when the agent's own InitiativePolicy permits the action
        // (default Allowed=Autonomous, no BlockedActions) and the unit's
        // skill-policy is silent, the unit's initiative-policy DENY overlay
        // wins. The dispatch is suppressed and a ReflectionActionSkipped
        // event surfaces the denying unit so operators can audit.
        _unitPolicyEnforcer
            .EvaluateInitiativeActionAsync(Arg.Any<string>(), "send-message", Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Deny("send-message blocked by unit initiative policy", "engineering"));

        var outcome = new ReflectionOutcome(true, "send-message", "because",
            JsonSerializer.SerializeToElement(new { }));
        ArrangeOutcome(outcome);
        ArrangeHandler("send-message", TranslatedMessage());

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _router.DidNotReceive().RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ReflectionActionSkipped &&
                e.Summary.Contains("BlockedByUnitInitiativePolicy")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reflection_AgentInitiativeAllows_UnitInitiativeBlocks_DenyWins()
    {
        // Agent-level policy explicitly allows "send-message". Unit-level
        // policy blocks it. The DENY-overlay rule (#250) means the action
        // must NOT dispatch.
        _policyStore.GetPolicyAsync($"agent:{AgentId}", Arg.Any<CancellationToken>())
            .Returns(new InitiativePolicy(
                MaxLevel: InitiativeLevel.Autonomous,
                AllowedActions: new[] { "send-message" }));

        _unitPolicyEnforcer
            .EvaluateInitiativeActionAsync(AgentId, "send-message", Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Deny("blocked by unit", "engineering"));

        var outcome = new ReflectionOutcome(true, "send-message", "because",
            JsonSerializer.SerializeToElement(new { }));
        ArrangeOutcome(outcome);
        ArrangeHandler("send-message", TranslatedMessage());

        await _actor.ReceiveReminderAsync(AgentActor.InitiativeReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromHours(1));

        await _router.DidNotReceive().RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
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
}