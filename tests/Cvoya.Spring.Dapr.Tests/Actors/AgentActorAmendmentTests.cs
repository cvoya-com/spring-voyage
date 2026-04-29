// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Reflection;
using System.Text.Json;

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
/// Tests for mid-flight amendment handling on <see cref="AgentActor"/> (#142).
/// Covers acceptance / rejection paths, priority-driven actor state changes,
/// and the log-and-drop behaviour when a parent unit has disabled the agent.
/// </summary>
public class AgentActorAmendmentTests
{
    private const string AgentId = "ada";
    private const string UnitId = "engineering";

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
    private readonly IAgentInitiativeEvaluator _initiativeEvaluator = Substitute.For<IAgentInitiativeEvaluator>();
    private readonly AgentActor _actor;

    public AgentActorAmendmentTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory);

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId(AgentId),
        });

        _membershipRepository
            .GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        _unitPolicyEnforcer.WithAllowByDefault();
        _initiativeEvaluator.WithActAutonomouslyByDefault();

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
            _initiativeEvaluator,
            loggerFactory);

        SetStateManager(_actor, _stateManager);

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(false, default!));
        _stateManager.TryGetStateAsync<List<PendingAmendment>>(StateKeys.AgentPendingAmendments, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<PendingAmendment>>(false, default!));
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

    private static Message CreateAmendment(
        Address from,
        string text = "please rebase before pushing",
        AmendmentPriority priority = AmendmentPriority.Informational,
        string? threadId = "conv-live")
    {
        var payload = JsonSerializer.SerializeToElement(new AmendmentPayload(
            Text: text,
            Priority: priority,
            CorrelationId: threadId));

        return new Message(
            Guid.NewGuid(),
            from,
            new Address("agent", AgentId),
            MessageType.Amendment,
            threadId,
            payload,
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Amendment_FromParentUnit_AcceptedAndQueued()
    {
        _membershipRepository.GetAsync(UnitId, AgentId, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitId, AgentId));

        var message = CreateAmendment(new Address("unit", UnitId));
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.Received().SetStateAsync(
            StateKeys.AgentPendingAmendments,
            Arg.Is<List<PendingAmendment>>(list => list.Count == 1 && list[0].Id == message.Id),
            Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.AmendmentReceived),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Amendment_FromNonMemberUnit_Rejected()
    {
        _membershipRepository.GetAsync(UnitId, AgentId, Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        var message = CreateAmendment(new Address("unit", UnitId));
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentPendingAmendments,
            Arg.Any<List<PendingAmendment>>(),
            Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.AmendmentRejected),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Amendment_FromSelf_Accepted()
    {
        var message = CreateAmendment(new Address("agent", AgentId));
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.Received().SetStateAsync(
            StateKeys.AgentPendingAmendments,
            Arg.Any<List<PendingAmendment>>(),
            Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.AmendmentReceived),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Amendment_FromAnotherAgent_Rejected()
    {
        var message = CreateAmendment(new Address("agent", "somebody-else"));
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentPendingAmendments,
            Arg.Any<List<PendingAmendment>>(),
            Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.AmendmentRejected),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Amendment_FromUnitWhereAgentDisabled_LogAndDrop()
    {
        _membershipRepository.GetAsync(UnitId, AgentId, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitId, AgentId, Enabled: false));

        var message = CreateAmendment(new Address("unit", UnitId));
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentPendingAmendments,
            Arg.Any<List<PendingAmendment>>(),
            Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.AmendmentRejected),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Amendment_MalformedPayload_Rejected()
    {
        var message = new Message(
            Guid.NewGuid(),
            new Address("agent", AgentId),
            new Address("agent", AgentId),
            MessageType.Amendment,
            "conv-live",
            JsonSerializer.SerializeToElement(new { not = "an amendment" }),
            DateTimeOffset.UtcNow);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentPendingAmendments,
            Arg.Any<List<PendingAmendment>>(),
            Arg.Any<CancellationToken>());
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.AmendmentRejected),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Amendment_StopAndWaitPriority_SetsPausedFlag()
    {
        var message = CreateAmendment(new Address("agent", AgentId),
            priority: AmendmentPriority.StopAndWait);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.Received().SetStateAsync(
            StateKeys.AgentPaused,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Amendment_InformationalPriority_DoesNotPause()
    {
        var message = CreateAmendment(new Address("agent", AgentId),
            priority: AmendmentPriority.Informational);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentPaused,
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Amendment_AppendsToExistingPendingList()
    {
        var existing = new PendingAmendment(
            Guid.NewGuid(),
            new Address("agent", AgentId),
            "prior",
            AmendmentPriority.Informational,
            null,
            DateTimeOffset.UtcNow);

        _stateManager.TryGetStateAsync<List<PendingAmendment>>(StateKeys.AgentPendingAmendments, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<PendingAmendment>>(true, [existing]));

        var message = CreateAmendment(new Address("agent", AgentId), text: "new one");
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.Received().SetStateAsync(
            StateKeys.AgentPendingAmendments,
            Arg.Is<List<PendingAmendment>>(list =>
                list.Count == 2 &&
                list[0].Id == existing.Id &&
                list[1].Id == message.Id),
            Arg.Any<CancellationToken>());
    }
}