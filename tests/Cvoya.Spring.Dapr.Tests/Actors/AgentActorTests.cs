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
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Routing;

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
    private readonly IInitiativeEngine _initiativeEngine = Substitute.For<IInitiativeEngine>();
    private readonly IAgentPolicyStore _policyStore = Substitute.For<IAgentPolicyStore>();
    private readonly IExecutionDispatcher _dispatcher = Substitute.For<IExecutionDispatcher>();
    private readonly MessageRouter _router;
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
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
        _actor = new AgentActor(
            host,
            _activityEventBus,
            _initiativeEngine,
            _policyStore,
            _dispatcher,
            _router,
            _definitionProvider,
            Array.Empty<ISkillRegistry>(),
            _loggerFactory);
        SetStateManager(_actor, _stateManager);

        // Default: no active conversation, no pending conversations.
        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(false, default!));
        _stateManager.TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ConversationChannel>>(false, default!));
    }

    private static Message CreateMessage(
        MessageType type = MessageType.Domain,
        string? conversationId = null,
        JsonElement? payload = null)
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", "test-sender"),
            new Address("agent", "test-agent"),
            type,
            conversationId ?? Guid.NewGuid().ToString(),
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
    public async Task ReceiveAsync_DomainMessageNewConversation_CreatesConversationChannel()
    {
        var conversationId = "conv-1";
        var message = CreateMessage(conversationId: conversationId);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c => c.ConversationId == conversationId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageExistingConversation_RoutesToChannel()
    {
        var conversationId = "conv-existing";
        var existingChannel = new ConversationChannel
        {
            ConversationId = conversationId,
            Messages = [CreateMessage(conversationId: conversationId)]
        };

        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, existingChannel));

        var newMessage = CreateMessage(conversationId: conversationId);
        var result = await _actor.ReceiveAsync(newMessage, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await _stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c =>
                c.ConversationId == conversationId &&
                c.Messages.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageDifferentConversation_QueuedAsPending()
    {
        var activeConversationId = "conv-active";
        var pendingConversationId = "conv-pending";
        var activeChannel = new ConversationChannel
        {
            ConversationId = activeConversationId,
            Messages = [CreateMessage(conversationId: activeConversationId)]
        };

        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));

        var message = CreateMessage(conversationId: pendingConversationId);
        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await _stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ConversationChannel>>(list =>
                list.Count == 1 &&
                list[0].ConversationId == pendingConversationId),
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
        var activeChannel = new ConversationChannel
        {
            ConversationId = "conv-active",
            Messages = []
        };
        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));

        var message = CreateMessage(type: MessageType.StatusQuery);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().ShouldBe("Active");
        payload.GetProperty("ActiveConversationId").GetString().ShouldBe("conv-active");
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
        var conversationId = "conv-first";
        var message = CreateMessage(conversationId: conversationId);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c => c.ConversationId == conversationId),
            Arg.Any<CancellationToken>());

        // Should NOT have set pending conversations.
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Any<List<ConversationChannel>>(),
            Arg.Any<CancellationToken>());
    }

    // --- Suspension/Resume Tests ---

    [Fact]
    public async Task SuspendActiveConversation_MovesActiveToPending()
    {
        var activeChannel = new ConversationChannel
        {
            ConversationId = "conv-active",
            Messages = []
        };

        // First activate a conversation to set up the CancellationTokenSource.
        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ConversationChannel>(false, default!),
                new ConditionalValue<ConversationChannel>(true, activeChannel));

        var message = CreateMessage(conversationId: "conv-active");
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        // Now reconfigure state to have the active conversation for suspend.
        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));

        await _actor.SuspendActiveConversationAsync(TestContext.Current.CancellationToken);

        await _stateManager.Received().TryRemoveStateAsync(StateKeys.ActiveConversation, Arg.Any<CancellationToken>());
        await _stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ConversationChannel>>(list =>
                list.Count == 1 &&
                list[0].ConversationId == "conv-active"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteNextPending_PromotesFirstPendingToActive()
    {
        var pendingChannel = new ConversationChannel
        {
            ConversationId = "conv-pending-1",
            Messages = []
        };
        _stateManager.TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ConversationChannel>>(true, [pendingChannel]));

        await _actor.PromoteNextPendingAsync(TestContext.Current.CancellationToken);

        await _stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c => c.ConversationId == "conv-pending-1"),
            Arg.Any<CancellationToken>());
        await _stateManager.Received().TryRemoveStateAsync(StateKeys.PendingConversations, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteNextPending_MultiplePending_KeepsRemainingInList()
    {
        var pending1 = new ConversationChannel { ConversationId = "conv-p1", Messages = [] };
        var pending2 = new ConversationChannel { ConversationId = "conv-p2", Messages = [] };
        _stateManager.TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ConversationChannel>>(true, [pending1, pending2]));

        await _actor.PromoteNextPendingAsync(TestContext.Current.CancellationToken);

        await _stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c => c.ConversationId == "conv-p1"),
            Arg.Any<CancellationToken>());
        await _stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ConversationChannel>>(list =>
                list.Count == 1 &&
                list[0].ConversationId == "conv-p2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuspendActiveConversation_NoActiveConversation_DoesNothing()
    {
        await _actor.SuspendActiveConversationAsync(TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().TryRemoveStateAsync(StateKeys.ActiveConversation, Arg.Any<CancellationToken>());
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Any<List<ConversationChannel>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteNextPending_NoPending_DoesNothing()
    {
        await _actor.PromoteNextPendingAsync(TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Any<ConversationChannel>(),
            Arg.Any<CancellationToken>());
    }

    // --- Cancel Handling Tests ---

    [Fact]
    public async Task ReceiveAsync_CancelMessage_ReturnsAcknowledgment()
    {
        var conversationId = "conv-cancel";
        var cancelMessage = CreateMessage(type: MessageType.Cancel, conversationId: conversationId);

        var result = await _actor.ReceiveAsync(cancelMessage, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReceiveAsync_CancelActiveConversation_RemovesActiveAndPromotesPending()
    {
        var activeChannel = new ConversationChannel
        {
            ConversationId = "conv-to-cancel",
            Messages = []
        };
        var pendingChannel = new ConversationChannel
        {
            ConversationId = "conv-pending",
            Messages = []
        };

        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));
        _stateManager.TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ConversationChannel>>(true, [pendingChannel]));

        var cancelMessage = CreateMessage(type: MessageType.Cancel, conversationId: "conv-to-cancel");
        await _actor.ReceiveAsync(cancelMessage, TestContext.Current.CancellationToken);

        await _stateManager.Received().TryRemoveStateAsync(StateKeys.ActiveConversation, Arg.Any<CancellationToken>());
        await _stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c => c.ConversationId == "conv-pending"),
            Arg.Any<CancellationToken>());
    }

    // --- Domain Message Validation ---

    [Fact]
    public async Task ReceiveAsync_DomainMessageWithExistingPendingConversation_AppendsToExistingPending()
    {
        var activeChannel = new ConversationChannel
        {
            ConversationId = "conv-active",
            Messages = []
        };
        var existingPending = new ConversationChannel
        {
            ConversationId = "conv-pending",
            Messages = [CreateMessage(conversationId: "conv-pending")]
        };

        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));
        _stateManager.TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ConversationChannel>>(true, [existingPending]));

        var message = CreateMessage(conversationId: "conv-pending");
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ConversationChannel>>(list =>
                list.Count == 1 &&
                list[0].ConversationId == "conv-pending" &&
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
        var message = CreateMessage(conversationId: "conv-activity");

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
    public async Task ReceiveAsync_ActivityEventBusFailure_DoesNotBreakActor()
    {
        _activityEventBus.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Bus down")));

        var message = CreateMessage(conversationId: "conv-bus-fail");

        // Should not throw even though the bus fails.
        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReceiveAsync_NewConversation_EmitsConversationStartedEvent()
    {
        var message = CreateMessage(conversationId: "conv-started");

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ConversationStarted &&
                e.CorrelationId == "conv-started"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_NewConversation_EmitsStateChangedIdleToActive()
    {
        var message = CreateMessage(conversationId: "conv-state-change");

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
        var activeChannel = new ConversationChannel
        {
            ConversationId = "conv-active",
            Messages = []
        };

        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));

        var message = CreateMessage(conversationId: "conv-new");
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.DecisionMade &&
                e.Summary.Contains("Queued") &&
                e.CorrelationId == "conv-new"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_CancelActiveConversation_EmitsConversationCompletedEvent()
    {
        var activeChannel = new ConversationChannel
        {
            ConversationId = "conv-to-complete",
            Messages = []
        };

        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));

        var cancelMessage = CreateMessage(type: MessageType.Cancel, conversationId: "conv-to-complete");
        await _actor.ReceiveAsync(cancelMessage, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ConversationCompleted &&
                e.CorrelationId == "conv-to-complete"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_CancelWithNoPending_EmitsStateChangedActiveToIdle()
    {
        var activeChannel = new ConversationChannel
        {
            ConversationId = "conv-cancel-idle",
            Messages = []
        };

        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ConversationChannel>(true, activeChannel),
                new ConditionalValue<ConversationChannel>(false, default!));

        var cancelMessage = CreateMessage(type: MessageType.Cancel, conversationId: "conv-cancel-idle");
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
        var activeChannel = new ConversationChannel
        {
            ConversationId = "conv-suspend",
            Messages = []
        };

        // First activate a conversation to set up the CancellationTokenSource.
        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ConversationChannel>(false, default!),
                new ConditionalValue<ConversationChannel>(true, activeChannel));

        var message = CreateMessage(conversationId: "conv-suspend");
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));

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
}