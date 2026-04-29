// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using global::Dapr.Actors.Runtime;

using NSubstitute;

using Shouldly;

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
        var threadId = "conv-first";
        var message = MessageFactory.CreateDomainMessage(threadId: threadId, toId: "mailbox-agent");

        var result = await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await stateManager.Received(1).SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ThreadChannel>(c => c.ThreadId == threadId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_SameThreadId_AppendsToActiveThread()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("mailbox-agent");
        var threadId = "conv-append";

        // First message creates the active conversation.
        var firstMessage = MessageFactory.CreateDomainMessage(threadId: threadId, toId: "mailbox-agent");
        var activeChannel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [firstMessage]
        };

        // After first message, reconfigure state to have the active conversation.
        stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));

        var secondMessage = MessageFactory.CreateDomainMessage(threadId: threadId, toId: "mailbox-agent");
        var result = await actor.ReceiveAsync(secondMessage, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ThreadChannel>(c =>
                c.ThreadId == threadId &&
                c.Messages.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DifferentThreadId_QueuedAsPending()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("mailbox-agent");
        var activeThreadId = "conv-active";
        var pendingThreadId = "conv-pending";

        var activeChannel = new ThreadChannel
        {
            ThreadId = activeThreadId,
            Messages = [MessageFactory.CreateDomainMessage(threadId: activeThreadId)]
        };

        stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));

        var pendingMessage = MessageFactory.CreateDomainMessage(threadId: pendingThreadId, toId: "mailbox-agent");
        var result = await actor.ReceiveAsync(pendingMessage, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ThreadChannel>>(list =>
                list.Count == 1 &&
                list[0].ThreadId == pendingThreadId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_StatusQuery_ReturnsCorrectActivePendingCounts()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("mailbox-agent");

        // Set up active conversation.
        var activeChannel = new ThreadChannel
        {
            ThreadId = "conv-active",
            Messages = []
        };
        stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));

        // Set up two pending conversations.
        var pending1 = new ThreadChannel { ThreadId = "conv-p1", Messages = [] };
        var pending2 = new ThreadChannel { ThreadId = "conv-p2", Messages = [] };
        stateManager.TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ThreadChannel>>(true, [pending1, pending2]));

        var statusQuery = MessageFactory.CreateStatusQuery("requester", "mailbox-agent");
        var result = await actor.ReceiveAsync(statusQuery, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.StatusQuery);

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().ShouldBe("Active");
        payload.GetProperty("ActiveThreadId").GetString().ShouldBe("conv-active");
        payload.GetProperty("PendingConversationCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task ReceiveAsync_StatusQueryWhenIdle_ReturnsIdleWithZeroPending()
    {
        var (actor, _) = ActorTestHost.CreateAgentActor("idle-agent");

        var statusQuery = MessageFactory.CreateStatusQuery("requester", "idle-agent");
        var result = await actor.ReceiveAsync(statusQuery, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().ShouldBe("Idle");
        payload.GetProperty("PendingConversationCount").GetInt32().ShouldBe(0);
    }
}