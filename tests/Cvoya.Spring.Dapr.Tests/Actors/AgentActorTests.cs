// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using FluentAssertions;
using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentActor"/> covering message routing,
/// control priority, conversation lifecycle, suspension/resume, and cancel handling.
/// </summary>
public class AgentActorTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly AgentActor _actor;

    public AgentActorTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId("test-agent")
        });
        _actor = new AgentActor(host, _loggerFactory);
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

        result.Should().NotBeNull();
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

        result.Should().NotBeNull();
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

        result.Should().NotBeNull();
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

        result.Should().NotBeNull();
        result!.Type.Should().Be(MessageType.StatusQuery);
        result.From.Should().Be(new Address("agent", "test-agent"));
        result.To.Should().Be(new Address("agent", "test-sender"));

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().Should().Be("Idle");
        payload.GetProperty("PendingConversationCount").GetInt32().Should().Be(0);
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

        result.Should().NotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().Should().Be("Active");
        payload.GetProperty("ActiveConversationId").GetString().Should().Be("conv-active");
    }

    [Fact]
    public async Task ReceiveAsync_HealthCheckMessage_ReturnsHealthy()
    {
        var message = CreateMessage(type: MessageType.HealthCheck);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Type.Should().Be(MessageType.HealthCheck);
        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Healthy").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_PolicyUpdateMessage_StoresPolicy()
    {
        var policyPayload = JsonSerializer.SerializeToElement(new { MaxConcurrency = 5 });
        var message = CreateMessage(type: MessageType.PolicyUpdate, payload: policyPayload);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
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

        result.Should().NotBeNull();
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
}
