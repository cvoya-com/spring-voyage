// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;
using global::Dapr.Actors.Runtime;
using FluentAssertions;
using NSubstitute;
using Xunit;

/// <summary>
/// Integration tests verifying agent mailbox routing: active conversation management,
/// pending conversation queuing, and status query accuracy.
/// </summary>
public class AgentMailboxRoutingTests
{
    [Fact]
    public async Task ReceiveAsync_FirstDomainMessage_BecomesActiveConversation()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("mailbox-agent");
        var conversationId = "conv-first";
        var message = MessageFactory.CreateDomainMessage(conversationId: conversationId, toId: "mailbox-agent");

        var result = await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        await stateManager.Received(1).SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c => c.ConversationId == conversationId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_SameConversationId_AppendsToActiveConversation()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("mailbox-agent");
        var conversationId = "conv-append";

        // First message creates the active conversation.
        var firstMessage = MessageFactory.CreateDomainMessage(conversationId: conversationId, toId: "mailbox-agent");
        var activeChannel = new ConversationChannel
        {
            ConversationId = conversationId,
            Messages = [firstMessage]
        };

        // After first message, reconfigure state to have the active conversation.
        stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));

        var secondMessage = MessageFactory.CreateDomainMessage(conversationId: conversationId, toId: "mailbox-agent");
        var result = await actor.ReceiveAsync(secondMessage, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        await stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c =>
                c.ConversationId == conversationId &&
                c.Messages.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DifferentConversationId_QueuedAsPending()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("mailbox-agent");
        var activeConversationId = "conv-active";
        var pendingConversationId = "conv-pending";

        var activeChannel = new ConversationChannel
        {
            ConversationId = activeConversationId,
            Messages = [MessageFactory.CreateDomainMessage(conversationId: activeConversationId)]
        };

        stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));

        var pendingMessage = MessageFactory.CreateDomainMessage(conversationId: pendingConversationId, toId: "mailbox-agent");
        var result = await actor.ReceiveAsync(pendingMessage, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        await stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ConversationChannel>>(list =>
                list.Count == 1 &&
                list[0].ConversationId == pendingConversationId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_StatusQuery_ReturnsCorrectActivePendingCounts()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("mailbox-agent");

        // Set up active conversation.
        var activeChannel = new ConversationChannel
        {
            ConversationId = "conv-active",
            Messages = []
        };
        stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));

        // Set up two pending conversations.
        var pending1 = new ConversationChannel { ConversationId = "conv-p1", Messages = [] };
        var pending2 = new ConversationChannel { ConversationId = "conv-p2", Messages = [] };
        stateManager.TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ConversationChannel>>(true, [pending1, pending2]));

        var statusQuery = MessageFactory.CreateStatusQuery("requester", "mailbox-agent");
        var result = await actor.ReceiveAsync(statusQuery, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Type.Should().Be(MessageType.StatusQuery);

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().Should().Be("Active");
        payload.GetProperty("ActiveConversationId").GetString().Should().Be("conv-active");
        payload.GetProperty("PendingConversationCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ReceiveAsync_StatusQueryWhenIdle_ReturnsIdleWithZeroPending()
    {
        var (actor, _) = ActorTestHost.CreateAgentActor("idle-agent");

        var statusQuery = MessageFactory.CreateStatusQuery("requester", "idle-agent");
        var result = await actor.ReceiveAsync(statusQuery, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().Should().Be("Idle");
        payload.GetProperty("PendingConversationCount").GetInt32().Should().Be(0);
    }
}
