// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;
using global::Dapr.Actors.Runtime;
using FluentAssertions;
using NSubstitute;
using Xunit;

/// <summary>
/// Integration tests simulating a GitHub webhook payload flowing through
/// a UnitActor to an AgentActor via an orchestration strategy.
/// </summary>
public class GitHubWebhookFlowTests
{
    [Fact]
    public async Task WebhookMessage_RoutedThroughUnit_StrategyReceivesWebhookPayload()
    {
        var (unitActor, unitStateManager, strategy) = ActorTestHost.CreateUnitActor(actorId: "webhook-unit");

        // Register an agent member on the unit.
        var agentAddress = new Address("agent", "webhook-agent");
        unitStateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [agentAddress]));

        // Capture what the strategy receives.
        Message? capturedMessage = null;
        IUnitContext? capturedContext = null;
        strategy.OrchestrateAsync(Arg.Any<Message>(), Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedMessage = callInfo.ArgAt<Message>(0);
                capturedContext = callInfo.ArgAt<IUnitContext>(1);
                return Task.FromResult<Message?>(null);
            });

        var webhookMessage = MessageFactory.CreateWebhookMessage(toId: "webhook-unit");

        await unitActor.ReceiveAsync(webhookMessage, TestContext.Current.CancellationToken);

        // Verify the strategy received the webhook message with its payload intact.
        capturedMessage.Should().NotBeNull();
        capturedMessage!.From.Scheme.Should().Be("connector");
        capturedMessage.From.Path.Should().Be("github-connector");

        var payload = capturedMessage.Payload.Deserialize<JsonElement>();
        payload.GetProperty("EventType").GetString().Should().Be("issues");
        payload.GetProperty("Action").GetString().Should().Be("opened");
        payload.GetProperty("Repository").GetString().Should().Be("test-org/test-repo");

        // Verify the context contains the agent member.
        capturedContext.Should().NotBeNull();
        capturedContext!.Members.Should().ContainSingle().Which.Should().Be(agentAddress);
    }

    [Fact]
    public async Task WebhookMessage_StrategyForwardsToAgent_AgentReceivesMessage()
    {
        // Set up the unit actor with a strategy that forwards to the agent.
        var (unitActor, unitStateManager, strategy) = ActorTestHost.CreateUnitActor(actorId: "flow-unit");
        var (agentActor, agentStateManager) = ActorTestHost.CreateAgentActor("flow-agent");

        var agentAddress = new Address("agent", "flow-agent");
        unitStateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [agentAddress]));

        // Strategy creates a forwarded message and we simulate it being delivered to the agent.
        var webhookMessage = MessageFactory.CreateWebhookMessage(toId: "flow-unit");
        Message? forwardedMessage = null;

        strategy.OrchestrateAsync(Arg.Any<Message>(), Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var originalMessage = callInfo.ArgAt<Message>(0);
                // Create a forwarded message addressed to the agent.
                forwardedMessage = new Message(
                    Guid.NewGuid(),
                    new Address("unit", "flow-unit"),
                    agentAddress,
                    MessageType.Domain,
                    originalMessage.ConversationId,
                    originalMessage.Payload,
                    DateTimeOffset.UtcNow);
                return Task.FromResult<Message?>(forwardedMessage);
            });

        // Unit processes the webhook.
        var unitResult = await unitActor.ReceiveAsync(webhookMessage, TestContext.Current.CancellationToken);
        unitResult.Should().NotBeNull();

        // Now deliver the forwarded message to the agent.
        var agentResult = await agentActor.ReceiveAsync(forwardedMessage!, TestContext.Current.CancellationToken);

        // Verify the agent received and stored the message as its active conversation.
        agentResult.Should().NotBeNull();
        await agentStateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c =>
                c.ConversationId == webhookMessage.ConversationId &&
                c.Messages.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WebhookMessage_AgentReceivesForwardedPayload_PayloadPreserved()
    {
        var (agentActor, agentStateManager) = ActorTestHost.CreateAgentActor("payload-agent");

        // Create a webhook-style message directly addressed to the agent (simulating post-orchestration).
        var webhookPayload = JsonSerializer.SerializeToElement(new
        {
            EventType = "pull_request",
            Action = "closed",
            Repository = "test-org/test-repo",
            PullRequest = new { Number = 99, Merged = true }
        });

        var message = new Message(
            Guid.NewGuid(),
            new Address("unit", "test-unit"),
            new Address("agent", "payload-agent"),
            MessageType.Domain,
            "webhook-conv-1",
            webhookPayload,
            DateTimeOffset.UtcNow);

        var result = await agentActor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        await agentStateManager.Received().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Is<ConversationChannel>(c =>
                c.ConversationId == "webhook-conv-1" &&
                c.Messages.Count == 1 &&
                c.Messages[0].Payload.GetProperty("EventType").GetString() == "pull_request"),
            Arg.Any<CancellationToken>());
    }
}
