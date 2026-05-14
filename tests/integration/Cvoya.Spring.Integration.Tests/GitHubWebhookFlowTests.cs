// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using global::Dapr.Actors.Runtime;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests simulating a GitHub webhook payload flowing through
/// a UnitActor runtime invocation path.
/// </summary>
public class GitHubWebhookFlowTests
{
    [Fact]
    public async Task WebhookMessage_RoutedThroughUnit_RuntimePathReceivesWebhookPayload()
    {
        var (unitActor, _, runtimeInvocationPath, graph) = ActorTestHost.CreateUnitActor(actorId: "webhook-unit");

        // Register an agent member on the unit (#2052: EF-backed graph).
        graph.SeedAgentMembers(TestSlugIds.For("webhook-unit"), TestSlugIds.For("webhook-agent"));

        // Capture what the runtime path receives.
        Message? capturedMessage = null;
        runtimeInvocationPath
            .InvokeAsync(
                Arg.Any<Address>(),
                Arg.Any<Message>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<Func<ActivityEvent, CancellationToken, Task>?>())
            .Returns(callInfo =>
            {
                capturedMessage = callInfo.ArgAt<Message>(1);
                return Task.CompletedTask;
            });

        var webhookMessage = MessageFactory.CreateWebhookMessage(toId: "webhook-unit");

        await unitActor.ReceiveAsync(webhookMessage, TestContext.Current.CancellationToken);

        // Verify the runtime path received the webhook message with its payload intact.
        capturedMessage.ShouldNotBeNull();
        capturedMessage!.From.Scheme.ShouldBe("connector");
        // MessageFactory.CreateWebhookMessage stamps the synthetic
        // DefaultConnectorId Guid; assert the hex form rather than the
        // legacy slug name.
        capturedMessage.From.Path.ShouldBe("cccccccc111111111111000000000001");

        var payload = capturedMessage.Payload.Deserialize<JsonElement>();
        payload.GetProperty("EventType").GetString().ShouldBe("issues");
        payload.GetProperty("Action").GetString().ShouldBe("opened");
        payload.GetProperty("Repository").GetString().ShouldBe("test-org/test-repo");
    }

    [Fact]
    public async Task WebhookMessage_RoutedThroughUnit_InvokesUnitRuntime()
    {
        var (unitActor, _, runtimeInvocationPath, graph) = ActorTestHost.CreateUnitActor(actorId: "flow-unit");

        graph.SeedAgentMembers(TestSlugIds.For("flow-unit"), TestSlugIds.For("flow-agent"));

        var webhookMessage = MessageFactory.CreateWebhookMessage(toId: "flow-unit");

        // Unit processes the webhook.
        var unitResult = await unitActor.ReceiveAsync(webhookMessage, TestContext.Current.CancellationToken);
        unitResult.ShouldBeNull();

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("flow-unit")),
            webhookMessage,
            Arg.Any<CancellationToken>(),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>?>());
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
            Address.For("unit", TestSlugIds.HexFor("test-unit")),
            Address.For("agent", TestSlugIds.HexFor("payload-agent")),
            MessageType.Domain,
            "webhook-conv-1",
            webhookPayload,
            DateTimeOffset.UtcNow);

        var result = await agentActor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await agentStateManager.Received().SetStateAsync(
            StateKeys.ChannelPrefix + "webhook-conv-1",
            Arg.Is<ThreadChannel>(c =>
                c.ThreadId == "webhook-conv-1" &&
                c.Messages.Count == 1 &&
                c.Messages[0].Payload.GetProperty("EventType").GetString() == "pull_request"),
            Arg.Any<CancellationToken>());
    }
}
