// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Cloning;
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
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentActor"/> covering message routing,
/// control priority, conversation lifecycle, suspension/resume, and cancel handling.
/// </summary>
public class AgentActorTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IExecutionDispatcher _dispatcher = Substitute.For<IExecutionDispatcher>();
    private readonly MessageRouter _router;
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IUnitMembershipRepository _membershipRepository = Substitute.For<IUnitMembershipRepository>();
    private readonly IUnitPolicyEnforcer _unitPolicyEnforcer = Substitute.For<IUnitPolicyEnforcer>();
    private readonly AgentActor _actor;

    public AgentActorTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            _loggerFactory);
        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId("test-agent")
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
            new AgentDispatchCoordinator(_dispatcher, _router, Substitute.For<ILogger<AgentDispatchCoordinator>>()),
            _definitionProvider,
            Array.Empty<ISkillRegistry>(),
            _membershipRepository,
            _unitPolicyEnforcer,
            Substitute.For<IAgentInitiativeEvaluator>(),
            _loggerFactory,
            Substitute.For<IAgentLifecycleCoordinator>(),
            new AgentStateCoordinator(Substitute.For<ILogger<AgentStateCoordinator>>()),
            new AgentAmendmentCoordinator(Substitute.For<ILogger<AgentAmendmentCoordinator>>()),
            new AgentUnitPolicyCoordinator(Substitute.For<ILogger<AgentUnitPolicyCoordinator>>()));
        SetStateManager(_actor, _stateManager);

        // Default: no active conversation, no pending conversations.
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(false, default!));
        _stateManager.TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ThreadChannel>>(false, default!));
    }

    private static Message CreateMessage(
        MessageType type = MessageType.Domain,
        string? threadId = null,
        JsonElement? payload = null)
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", "test-sender"),
            new Address("agent", "test-agent"),
            type,
            threadId ?? Guid.NewGuid().ToString(),
            payload ?? JsonSerializer.SerializeToElement(new { }),
            DateTimeOffset.UtcNow);
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

    // --- Message Routing Tests ---

    [Fact]
    public async Task ReceiveAsync_DomainMessageNewConversation_CreatesThreadChannel()
    {
        var threadId = "conv-1";
        var message = CreateMessage(threadId: threadId);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ThreadChannel>(c => c.ThreadId == threadId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageExistingConversation_RoutesToChannel()
    {
        var threadId = "conv-existing";
        var existingChannel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [CreateMessage(threadId: threadId)]
        };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, existingChannel));

        var newMessage = CreateMessage(threadId: threadId);
        var result = await _actor.ReceiveAsync(newMessage, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await _stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ThreadChannel>(c =>
                c.ThreadId == threadId &&
                c.Messages.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageDifferentConversation_QueuedAsPending()
    {
        var activeThreadId = "conv-active";
        var pendingThreadId = "conv-pending";
        var activeChannel = new ThreadChannel
        {
            ThreadId = activeThreadId,
            Messages = [CreateMessage(threadId: activeThreadId)]
        };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));

        var message = CreateMessage(threadId: pendingThreadId);
        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await _stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ThreadChannel>>(list =>
                list.Count == 1 &&
                list[0].ThreadId == pendingThreadId),
            Arg.Any<CancellationToken>());
    }

    // --- Control Priority Tests ---

    [Fact]
    public async Task ReceiveAsync_StatusQueryMessage_ReturnsCurrentStatus()
    {
        var message = CreateMessage(type: MessageType.StatusQuery);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.StatusQuery);
        result.From.ShouldBe(new Address("agent", "test-agent"));
        result.To.ShouldBe(new Address("agent", "test-sender"));

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().ShouldBe("Idle");
        payload.GetProperty("PendingConversationCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task ReceiveAsync_StatusQueryWithActiveConversation_ReturnsActiveStatus()
    {
        var activeChannel = new ThreadChannel
        {
            ThreadId = "conv-active",
            Messages = []
        };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));

        var message = CreateMessage(type: MessageType.StatusQuery);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().ShouldBe("Active");
        payload.GetProperty("ActiveThreadId").GetString().ShouldBe("conv-active");
    }

    [Fact]
    public async Task ReceiveAsync_HealthCheckMessage_ReturnsHealthy()
    {
        var message = CreateMessage(type: MessageType.HealthCheck);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.HealthCheck);
        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Healthy").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_PolicyUpdateMessage_StoresPolicy()
    {
        var policyPayload = JsonSerializer.SerializeToElement(new { MaxConcurrency = 5 });
        var message = CreateMessage(type: MessageType.PolicyUpdate, payload: policyPayload);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await _stateManager.Received(1).SetStateAsync(
            "Agent:LastPolicyUpdate",
            Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>());
    }

    // --- Conversation Lifecycle Tests ---

    [Fact]
    public async Task ReceiveAsync_FirstDomainMessage_BecomesActiveConversation()
    {
        var threadId = "conv-first";
        var message = CreateMessage(threadId: threadId);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ThreadChannel>(c => c.ThreadId == threadId),
            Arg.Any<CancellationToken>());

        // Should NOT have set pending conversations.
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Any<List<ThreadChannel>>(),
            Arg.Any<CancellationToken>());
    }

    // --- Suspension/Resume Tests ---

    [Fact]
    public async Task SuspendActiveConversation_MovesActiveToPending()
    {
        var activeChannel = new ThreadChannel
        {
            ThreadId = "conv-active",
            Messages = []
        };

        // First activate a conversation to set up the CancellationTokenSource.
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ThreadChannel>(false, default!),
                new ConditionalValue<ThreadChannel>(true, activeChannel));

        var message = CreateMessage(threadId: "conv-active");
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        // Now reconfigure state to have the active conversation for suspend.
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));

        await _actor.SuspendActiveConversationAsync(TestContext.Current.CancellationToken);

        await _stateManager.Received().TryRemoveStateAsync(StateKeys.ActiveConversation, Arg.Any<CancellationToken>());
        await _stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ThreadChannel>>(list =>
                list.Count == 1 &&
                list[0].ThreadId == "conv-active"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteNextPending_PromotesFirstPendingToActive()
    {
        var pendingChannel = new ThreadChannel
        {
            ThreadId = "conv-pending-1",
            Messages = []
        };
        _stateManager.TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ThreadChannel>>(true, [pendingChannel]));

        await _actor.PromoteNextPendingAsync(TestContext.Current.CancellationToken);

        await _stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ThreadChannel>(c => c.ThreadId == "conv-pending-1"),
            Arg.Any<CancellationToken>());
        await _stateManager.Received().TryRemoveStateAsync(StateKeys.PendingConversations, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteNextPending_MultiplePending_KeepsRemainingInList()
    {
        var pending1 = new ThreadChannel { ThreadId = "conv-p1", Messages = [] };
        var pending2 = new ThreadChannel { ThreadId = "conv-p2", Messages = [] };
        _stateManager.TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ThreadChannel>>(true, [pending1, pending2]));

        await _actor.PromoteNextPendingAsync(TestContext.Current.CancellationToken);

        await _stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ThreadChannel>(c => c.ThreadId == "conv-p1"),
            Arg.Any<CancellationToken>());
        await _stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ThreadChannel>>(list =>
                list.Count == 1 &&
                list[0].ThreadId == "conv-p2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuspendActiveConversation_NoActiveConversation_DoesNothing()
    {
        await _actor.SuspendActiveConversationAsync(TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().TryRemoveStateAsync(StateKeys.ActiveConversation, Arg.Any<CancellationToken>());
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Any<List<ThreadChannel>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteNextPending_NoPending_DoesNothing()
    {
        await _actor.PromoteNextPendingAsync(TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Any<ThreadChannel>(),
            Arg.Any<CancellationToken>());
    }

    // --- Cancel Handling Tests ---

    [Fact]
    public async Task ReceiveAsync_CancelMessage_ReturnsAcknowledgment()
    {
        var threadId = "conv-cancel";
        var cancelMessage = CreateMessage(type: MessageType.Cancel, threadId: threadId);

        var result = await _actor.ReceiveAsync(cancelMessage, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReceiveAsync_CancelActiveConversation_RemovesActiveAndPromotesPending()
    {
        var activeChannel = new ThreadChannel
        {
            ThreadId = "conv-to-cancel",
            Messages = []
        };
        var pendingChannel = new ThreadChannel
        {
            ThreadId = "conv-pending",
            Messages = []
        };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));
        _stateManager.TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ThreadChannel>>(true, [pendingChannel]));

        var cancelMessage = CreateMessage(type: MessageType.Cancel, threadId: "conv-to-cancel");
        await _actor.ReceiveAsync(cancelMessage, TestContext.Current.CancellationToken);

        await _stateManager.Received().TryRemoveStateAsync(StateKeys.ActiveConversation, Arg.Any<CancellationToken>());
        await _stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ThreadChannel>(c => c.ThreadId == "conv-pending"),
            Arg.Any<CancellationToken>());
    }

    // --- Domain Message Validation ---

    [Fact]
    public async Task ReceiveAsync_DomainMessageWithExistingPendingConversation_AppendsToExistingPending()
    {
        var activeChannel = new ThreadChannel
        {
            ThreadId = "conv-active",
            Messages = []
        };
        var existingPending = new ThreadChannel
        {
            ThreadId = "conv-pending",
            Messages = [CreateMessage(threadId: "conv-pending")]
        };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));
        _stateManager.TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ThreadChannel>>(true, [existingPending]));

        var message = CreateMessage(threadId: "conv-pending");
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ThreadChannel>>(list =>
                list.Count == 1 &&
                list[0].ThreadId == "conv-pending" &&
                list[0].Messages.Count == 2),
            Arg.Any<CancellationToken>());
    }

    // --- Clone Awareness Tests ---

    [Fact]
    public async Task IsCloneAsync_NoCloneIdentity_ReturnsFalse()
    {
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(false, default!));

        var result = await _actor.IsCloneAsync(TestContext.Current.CancellationToken);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task IsCloneAsync_HasCloneIdentity_ReturnsTrue()
    {
        var identity = new CloneIdentity("parent-agent", "test-agent",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(true, identity));

        var result = await _actor.IsCloneAsync(TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task GetCloneIdentityAsync_NoCloneIdentity_ReturnsNull()
    {
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(false, default!));

        var result = await _actor.GetCloneIdentityAsync(TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetCloneIdentityAsync_HasCloneIdentity_ReturnsIdentity()
    {
        var identity = new CloneIdentity("parent-agent", "test-agent",
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(true, identity));

        var result = await _actor.GetCloneIdentityAsync(TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.ParentAgentId.ShouldBe("parent-agent");
        result.CloneId.ShouldBe("test-agent");
        result.CloningPolicy.ShouldBe(CloningPolicy.EphemeralWithMemory);
        result.AttachmentMode.ShouldBe(AttachmentMode.Attached);
    }

    [Fact]
    public async Task GetCostAttributionTargetAsync_IsClone_ReturnsParentId()
    {
        var identity = new CloneIdentity("parent-agent", "test-agent",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(true, identity));

        var result = await _actor.GetCostAttributionTargetAsync(TestContext.Current.CancellationToken);

        result.ShouldBe("parent-agent");
    }

    [Fact]
    public async Task GetCostAttributionTargetAsync_NotClone_ReturnsNull()
    {
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(false, default!));

        var result = await _actor.GetCostAttributionTargetAsync(TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    // --- Activity Event Emission Tests ---

    [Fact]
    public async Task ReceiveAsync_DomainMessage_EmitsMessageReceivedActivityEvent()
    {
        var message = CreateMessage(threadId: "conv-activity");

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.MessageReceived),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_ControlMessage_EmitsMessageReceivedActivityEvent()
    {
        var message = CreateMessage(type: MessageType.HealthCheck);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.MessageReceived),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_MessageReceived_StampsEnvelopeAndBodyOnDetails()
    {
        // #1209: the conversation projection / `spring message show` rely
        // on the MessageReceived event's Details JSON to carry the
        // sender / recipient / body. Regression-guard the shape so a
        // future refactor doesn't quietly drop fields the surfaces depend
        // on.
        var threadId = "conv-1209";
        var payload = JsonSerializer.SerializeToElement("hello world");
        var message = CreateMessage(threadId: threadId, payload: payload);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.MessageReceived
                && e.Details.HasValue
                && e.Details.Value.GetProperty("messageId").GetString() == message.Id.ToString()
                && e.Details.Value.GetProperty("from").GetString() == $"{message.From.Scheme}://{message.From.Path}"
                && e.Details.Value.GetProperty("to").GetString() == $"{message.To.Scheme}://{message.To.Path}"
                && e.Details.Value.GetProperty("body").GetString() == "hello world"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_ActivityEventBusFailure_DoesNotBreakActor()
    {
        _activityEventBus.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Bus down")));

        var message = CreateMessage(threadId: "conv-bus-fail");

        // Should not throw even though the bus fails.
        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReceiveAsync_NewThread_EmitsThreadStartedEvent()
    {
        var message = CreateMessage(threadId: "conv-started");

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ThreadStarted &&
                e.CorrelationId == "conv-started"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_NewConversation_EmitsStateChangedIdleToActive()
    {
        var message = CreateMessage(threadId: "conv-state-change");

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("Idle") &&
                e.Summary.Contains("Active")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DifferentConversationQueued_EmitsDecisionMadeEvent()
    {
        var activeChannel = new ThreadChannel
        {
            ThreadId = "conv-active",
            Messages = []
        };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));

        var message = CreateMessage(threadId: "conv-new");
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.DecisionMade &&
                e.Summary.Contains("Queued") &&
                e.CorrelationId == "conv-new"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_CancelActiveThread_EmitsThreadCompletedEvent()
    {
        var activeChannel = new ThreadChannel
        {
            ThreadId = "conv-to-complete",
            Messages = []
        };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));

        var cancelMessage = CreateMessage(type: MessageType.Cancel, threadId: "conv-to-complete");
        await _actor.ReceiveAsync(cancelMessage, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ThreadCompleted &&
                e.CorrelationId == "conv-to-complete"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_CancelWithNoPending_EmitsStateChangedActiveToIdle()
    {
        var activeChannel = new ThreadChannel
        {
            ThreadId = "conv-cancel-idle",
            Messages = []
        };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ThreadChannel>(true, activeChannel),
                new ConditionalValue<ThreadChannel>(false, default!));

        var cancelMessage = CreateMessage(type: MessageType.Cancel, threadId: "conv-cancel-idle");
        await _actor.ReceiveAsync(cancelMessage, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("Active") &&
                e.Summary.Contains("Idle")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuspendActiveConversation_EmitsStateChangedActiveToSuspended()
    {
        var activeChannel = new ThreadChannel
        {
            ThreadId = "conv-suspend",
            Messages = []
        };

        // First activate a conversation to set up the CancellationTokenSource.
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ThreadChannel>(false, default!),
                new ConditionalValue<ThreadChannel>(true, activeChannel));

        var message = CreateMessage(threadId: "conv-suspend");
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));

        await _actor.SuspendActiveConversationAsync(TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("Suspended")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmitCostIncurredAsync_EmitsCostEvent()
    {
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(false, default!));

        await _actor.EmitCostIncurredAsync(
            0.05m, "gpt-4", 1000, 500,
            Cvoya.Spring.Core.Costs.CostSource.Work,
            TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.CostIncurred &&
                e.Cost == 0.05m &&
                e.Details.HasValue &&
                e.Details.Value.GetProperty("costSource").GetString() == "Work"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmitCostIncurredAsync_Clone_IncludesParentAgentInDetails()
    {
        var identity = new CloneIdentity("parent-agent", "test-agent",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(true, identity));

        await _actor.EmitCostIncurredAsync(
            0.10m, "claude-3", 2000, 1000,
            Cvoya.Spring.Core.Costs.CostSource.Initiative,
            TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.CostIncurred &&
                e.Cost == 0.10m &&
                e.Details.HasValue &&
                e.Details.Value.GetProperty("parentAgentId").GetString() == "parent-agent" &&
                e.Details.Value.GetProperty("costSource").GetString() == "Initiative"),
            Arg.Any<CancellationToken>());
    }

    // --- #1036 / #1038 — non-zero dispatch exit + close API ---

    [Fact]
    public async Task RunDispatchAsync_NonZeroExitCode_EmitsErrorAndClearsActiveConversation()
    {
        // Arrange — dispatcher returns a response payload that mirrors the
        // shape A2AExecutionDispatcher.BuildResponseMessage emits when the
        // container exits non-zero. The first state read (during ReceiveAsync)
        // must report "no active conversation" so the actor takes the dispatch
        // path; subsequent reads (after dispatch returns) must report the
        // active conversation so ClearActiveConversationAsync has something to
        // clear.
        var threadId = "conv-exit-125";
        var activeChannel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = []
        };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ThreadChannel>(false, default!),
                new ConditionalValue<ThreadChannel>(true, activeChannel));

        var inbound = CreateMessage(threadId: threadId);
        var failurePayload = JsonSerializer.SerializeToElement(new
        {
            Error = "container init: image not found\nlayer 1 missing",
            Output = string.Empty,
            ExitCode = 125,
        });
        var failureResponse = new Message(
            Guid.NewGuid(),
            new Address("agent", "test-agent"),
            inbound.From,
            MessageType.Domain,
            threadId,
            failurePayload,
            DateTimeOffset.UtcNow);

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(failureResponse);
        _router.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Core.Result<Message?, RoutingError>.Success(null));

        // Act — the test harness omits IActorProxyFactory, so the dispatch
        // task falls through to calling ClearActiveConversationAsync
        // directly (mocked StateManager == no real concurrency to race).
        await _actor.ReceiveAsync(inbound, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        // Assert — ErrorOccurred event with structured details + active
        // conversation cleared + StateChanged Active→Idle event emitted.
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ErrorOccurred &&
                e.CorrelationId == threadId &&
                e.Summary.Contains("125") &&
                e.Details.HasValue &&
                e.Details.Value.GetProperty("exitCode").GetInt32() == 125),
            Arg.Any<CancellationToken>());

        await _stateManager.Received().TryRemoveStateAsync(
            StateKeys.ActiveConversation, Arg.Any<CancellationToken>());

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("Active") &&
                e.Summary.Contains("Idle")),
            Arg.Any<CancellationToken>());

        // Original sender still gets the failure response routed back
        // (so the human / upstream agent can see it).
        await _router.Received(1).RouteAsync(
            Arg.Is<Message>(m => m.Id == failureResponse.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloseConversationAsync_ActiveId_ClearsAndPromotesNextPending()
    {
        var active = new ThreadChannel { ThreadId = "conv-active", Messages = [] };
        var pending = new ThreadChannel { ThreadId = "conv-next", Messages = [] };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, active));
        _stateManager.TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ThreadChannel>>(true, [pending]));

        await _actor.CloseConversationAsync("conv-active", "operator request",
            TestContext.Current.CancellationToken);

        // Active state removed
        await _stateManager.Received().TryRemoveStateAsync(
            StateKeys.ActiveConversation, Arg.Any<CancellationToken>());

        // ConversationClosed event with structured details
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ThreadClosed &&
                e.CorrelationId == "conv-active" &&
                e.Details.HasValue &&
                e.Details.Value.GetProperty("wasActive").GetBoolean() == true &&
                e.Details.Value.GetProperty("reason").GetString() == "operator request"),
            Arg.Any<CancellationToken>());

        // Promotion ran — next pending now active
        await _stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ThreadChannel>(c => c.ThreadId == "conv-next"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloseConversationAsync_PendingId_RemovesPendingAndLeavesActiveAlone()
    {
        var active = new ThreadChannel { ThreadId = "conv-active", Messages = [] };
        var p1 = new ThreadChannel { ThreadId = "conv-keep", Messages = [] };
        var p2 = new ThreadChannel { ThreadId = "conv-drop", Messages = [] };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, active));
        _stateManager.TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ThreadChannel>>(true, [p1, p2]));

        await _actor.CloseConversationAsync("conv-drop", null,
            TestContext.Current.CancellationToken);

        // Active state must NOT have been removed
        await _stateManager.DidNotReceive().TryRemoveStateAsync(
            StateKeys.ActiveConversation, Arg.Any<CancellationToken>());

        // Pending list rewritten with conv-keep only
        await _stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ThreadChannel>>(list =>
                list.Count == 1 && list[0].ThreadId == "conv-keep"),
            Arg.Any<CancellationToken>());

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ThreadClosed &&
                e.CorrelationId == "conv-drop" &&
                e.Details.HasValue &&
                e.Details.Value.GetProperty("wasActive").GetBoolean() == false),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloseConversationAsync_UnknownId_IsNoOp()
    {
        var active = new ThreadChannel { ThreadId = "conv-active", Messages = [] };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, active));
        _stateManager.TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ThreadChannel>>(false, default!));

        await _actor.CloseConversationAsync("conv-unknown", "noop",
            TestContext.Current.CancellationToken);

        // No state mutation, no event emitted.
        await _stateManager.DidNotReceive().TryRemoveStateAsync(
            StateKeys.ActiveConversation, Arg.Any<CancellationToken>());
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Any<List<ThreadChannel>>(),
            Arg.Any<CancellationToken>());
        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.ThreadClosed),
            Arg.Any<CancellationToken>());
    }

    // --- post-Stage-2 (#1063 / #522) follow-ups: dispatch lifecycle ---

    /// <summary>
    /// Regression for the OCE branch in <c>RunDispatchAsync</c>: a cancelled
    /// dispatch (e.g. worker-side HttpClient timeout firing while the
    /// dispatcher is mid-run) MUST clear the active-conversation slot,
    /// otherwise every subsequent message in any other conversation gets
    /// queued as pending forever and the agent looks bricked. Discovered
    /// when the post-Stage-2 cutover surfaced the dispatcher-client's
    /// 100 s default timeout — the actor logged "Dispatch cancelled" and
    /// then refused every follow-up message.
    /// </summary>
    /// <summary>
    /// Regression for the success path in <c>RunDispatchAsync</c>: a
    /// dispatch that returns a non-null response with a zero (or absent)
    /// exit code MUST also clear the active-conversation slot, otherwise
    /// the actor sits permanently in Active state after replying — every
    /// subsequent message in another thread queues as pending forever
    /// (Case 3 in HandleDomainMessageAsync) and every reply to the same
    /// thread silently appends to a dead channel (Case 2). Discovered while
    /// debugging the agent Messages tab: a single successful turn left
    /// Status=Active, PendingConversationCount=0, and the agent
    /// stopped responding to anyone.
    /// </summary>
    [Fact]
    public async Task RunDispatchAsync_SuccessfulDispatch_ClearsActiveConversation()
    {
        var threadId = "conv-success";
        var activeChannel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = []
        };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ThreadChannel>(false, default!),
                new ConditionalValue<ThreadChannel>(true, activeChannel));

        var inbound = CreateMessage(threadId: threadId);
        var successPayload = JsonSerializer.SerializeToElement(new { Output = "ok" });
        var successResponse = new Message(
            Guid.NewGuid(),
            new Address("agent", "test-agent"),
            inbound.From,
            MessageType.Domain,
            threadId,
            successPayload,
            DateTimeOffset.UtcNow);

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(successResponse);
        _router.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Core.Result<Message?, RoutingError>.Success(null));

        await _actor.ReceiveAsync(inbound, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        // Active conversation must be cleared after a successful turn.
        await _stateManager.Received().TryRemoveStateAsync(
            StateKeys.ActiveConversation, Arg.Any<CancellationToken>());

        // The Active→Idle StateChanged event must be emitted with the
        // success-path reason so operators can correlate "turn finished
        // cleanly" against "turn aborted due to <X>".
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("Active") &&
                e.Summary.Contains("Idle") &&
                e.Summary.Contains("dispatch completed")),
            Arg.Any<CancellationToken>());

        // Original sender still gets the reply routed back.
        await _router.Received(1).RouteAsync(
            Arg.Is<Message>(m => m.Id == successResponse.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunDispatchAsync_CancelledDispatch_ClearsActiveConversation()
    {
        var threadId = "conv-cancelled";
        var activeChannel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = []
        };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ThreadChannel>(false, default!),
                new ConditionalValue<ThreadChannel>(true, activeChannel));

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns<Message?>(_ => throw new OperationCanceledException("simulated worker timeout"));

        var inbound = CreateMessage(threadId: threadId);

        await _actor.ReceiveAsync(inbound, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _stateManager.Received().TryRemoveStateAsync(
            StateKeys.ActiveConversation, Arg.Any<CancellationToken>());

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("Active") &&
                e.Summary.Contains("Idle") &&
                e.Summary.Contains("dispatch cancelled")),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression for <c>PromoteNextPendingAsync</c>: promoting a
    /// pending channel to active MUST also schedule a dispatch for the
    /// queued head message, otherwise the actor sits Active-but-idle
    /// after a clear/close and the user never gets a response. The
    /// first-message path in <c>HandleDomainMessageAsync</c> Case 1
    /// already does this; promotion has to mirror it.
    /// </summary>
    [Fact]
    public async Task CloseConversationAsync_PromotesAndDispatchesQueuedHead()
    {
        var queuedHead = CreateMessage(threadId: "conv-pending");
        var active = new ThreadChannel { ThreadId = "conv-active", Messages = [] };
        var pending = new ThreadChannel
        {
            ThreadId = "conv-pending",
            Messages = [queuedHead]
        };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, active));
        _stateManager.TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ThreadChannel>>(true, [pending]));

        // Dispatcher returns no response — keeps the assertion focused on
        // the "did we even call DispatchAsync for the head message?"
        // question without dragging routing/clear-on-failure into scope.
        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        await _actor.CloseConversationAsync("conv-active", "operator request",
            TestContext.Current.CancellationToken);

        if (_actor.PendingDispatchTask is not null)
        {
            await _actor.PendingDispatchTask;
        }

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<Message>(m => m.Id == queuedHead.Id),
            Arg.Any<PromptAssemblyContext?>(),
            Arg.Any<CancellationToken>());
    }
}