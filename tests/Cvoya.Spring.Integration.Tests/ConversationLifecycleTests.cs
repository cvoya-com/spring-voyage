// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using FluentAssertions;

using global::Dapr.Actors.Runtime;

using NSubstitute;

using Xunit;

/// <summary>
/// Integration tests covering the full conversation lifecycle:
/// initial activation, follow-up appending, pending queuing,
/// cancellation, and pending promotion.
/// </summary>
public class ConversationLifecycleTests
{
    [Fact]
    public async Task Lifecycle_InitialMessage_BecomesActive_FollowUpAppends()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("lifecycle-agent");
        var conversationId = "lifecycle-conv-1";

        // Step 1: Send initial domain message — becomes active conversation.
        var firstMessage = MessageFactory.CreateDomainMessage(conversationId: conversationId, toId: "lifecycle-agent");
        var result1 = await actor.ReceiveAsync(firstMessage, TestContext.Current.CancellationToken);
        result1.Should().NotBeNull();

        await stateManager.Received(1).SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c => c.ConversationId == conversationId && c.Messages.Count == 1),
            Arg.Any<CancellationToken>());

        // Simulate state with active conversation.
        var activeChannel = new ConversationChannel
        {
            ConversationId = conversationId,
            Messages = [firstMessage]
        };
        stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));

        // Step 2: Send follow-up on active conversation — appended.
        var followUpMessage = MessageFactory.CreateDomainMessage(conversationId: conversationId, toId: "lifecycle-agent");
        var result2 = await actor.ReceiveAsync(followUpMessage, TestContext.Current.CancellationToken);
        result2.Should().NotBeNull();

        await stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c =>
                c.ConversationId == conversationId &&
                c.Messages.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lifecycle_SecondConversation_QueuedAsPending()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("lifecycle-agent");
        var activeConvId = "active-conv";
        var pendingConvId = "pending-conv";

        // Set up active conversation.
        var activeChannel = new ConversationChannel
        {
            ConversationId = activeConvId,
            Messages = [MessageFactory.CreateDomainMessage(conversationId: activeConvId)]
        };
        stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));

        // Send message for second conversation.
        var pendingMessage = MessageFactory.CreateDomainMessage(conversationId: pendingConvId, toId: "lifecycle-agent");
        var result = await actor.ReceiveAsync(pendingMessage, TestContext.Current.CancellationToken);
        result.Should().NotBeNull();

        await stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ConversationChannel>>(list =>
                list.Count == 1 &&
                list[0].ConversationId == pendingConvId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lifecycle_CancelActive_ClearsActiveConversation()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("cancel-agent");
        var activeConvId = "cancel-conv";

        // First activate a conversation to set up the CancellationTokenSource.
        stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ConversationChannel>(false, default!));
        var initialMessage = MessageFactory.CreateDomainMessage(conversationId: activeConvId, toId: "cancel-agent");
        await actor.ReceiveAsync(initialMessage, TestContext.Current.CancellationToken);

        // Now set up state to have the active conversation.
        var activeChannel = new ConversationChannel
        {
            ConversationId = activeConvId,
            Messages = [initialMessage]
        };
        stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));

        // Cancel the active conversation.
        var cancelMessage = MessageFactory.CreateCancelMessage(activeConvId, "requester", "cancel-agent");
        var result = await actor.ReceiveAsync(cancelMessage, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        await stateManager.Received().TryRemoveStateAsync(StateKeys.ActiveConversation, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lifecycle_CancelActive_PromotesPendingToActive()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("promote-agent");
        var activeConvId = "conv-to-cancel";
        var pendingConvId = "conv-to-promote";

        // First activate a conversation to set up the CancellationTokenSource.
        stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(false, default!));
        var initialMessage = MessageFactory.CreateDomainMessage(conversationId: activeConvId, toId: "promote-agent");
        await actor.ReceiveAsync(initialMessage, TestContext.Current.CancellationToken);

        // Set up state: active conversation + one pending conversation.
        var activeChannel = new ConversationChannel
        {
            ConversationId = activeConvId,
            Messages = [initialMessage]
        };
        var pendingChannel = new ConversationChannel
        {
            ConversationId = pendingConvId,
            Messages = [MessageFactory.CreateDomainMessage(conversationId: pendingConvId)]
        };

        stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true, activeChannel));
        stateManager.TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ConversationChannel>>(true, [pendingChannel]));

        // Cancel the active conversation.
        var cancelMessage = MessageFactory.CreateCancelMessage(activeConvId, "requester", "promote-agent");
        await actor.ReceiveAsync(cancelMessage, TestContext.Current.CancellationToken);

        // Verify active was removed and pending was promoted.
        await stateManager.Received().TryRemoveStateAsync(StateKeys.ActiveConversation, Arg.Any<CancellationToken>());
        await stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c => c.ConversationId == pendingConvId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lifecycle_PromoteNextPending_WhenMultiplePending_PromotesFirstAndKeepsRest()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("multi-pending-agent");

        var pending1 = new ConversationChannel
        {
            ConversationId = "pending-1",
            Messages = [MessageFactory.CreateDomainMessage(conversationId: "pending-1")]
        };
        var pending2 = new ConversationChannel
        {
            ConversationId = "pending-2",
            Messages = [MessageFactory.CreateDomainMessage(conversationId: "pending-2")]
        };

        stateManager.TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ConversationChannel>>(true, [pending1, pending2]));

        await actor.PromoteNextPendingAsync(TestContext.Current.CancellationToken);

        // First pending promoted to active.
        await stateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c => c.ConversationId == "pending-1"),
            Arg.Any<CancellationToken>());

        // Second pending remains in the pending list.
        await stateManager.Received().SetStateAsync(
            StateKeys.PendingConversations,
            Arg.Is<List<ConversationChannel>>(list =>
                list.Count == 1 &&
                list[0].ConversationId == "pending-2"),
            Arg.Any<CancellationToken>());
    }
}