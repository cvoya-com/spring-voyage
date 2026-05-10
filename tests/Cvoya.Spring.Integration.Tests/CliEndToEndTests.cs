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
/// Integration tests simulating the CLI end-to-end lifecycle:
/// create unit, add agents, send messages, and check status.
/// </summary>
public class CliEndToEndTests
{
    [Fact]
    public async Task FullLifecycle_CreateUnit_AddAgents_SendMessage_CheckStatus()
    {
        // Step 1: Create a unit actor (#2052: EF-backed member graph).
        var (unitActor, _, runtimeInvocationPath, _) = ActorTestHost.CreateUnitActor(actorId: "cli-unit");

        // Step 2: Add agent members to the unit.
        var agent1 = new Address("agent", TestSlugIds.For("cli-agent-1"));
        var agent2 = new Address("agent", TestSlugIds.For("cli-agent-2"));

        await unitActor.AddMemberAsync(agent1, TestContext.Current.CancellationToken);
        await unitActor.AddMemberAsync(agent2, TestContext.Current.CancellationToken);

        // Verify members.
        var members = await unitActor.GetMembersAsync(TestContext.Current.CancellationToken);
        members.Length.ShouldBe(2);

        // Step 3: Send a domain message to the unit.
        var message = MessageFactory.CreateDomainMessage(toId: "cli-unit", toType: "unit");

        await unitActor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("cli-unit")),
            message,
            Arg.Any<CancellationToken>());

        // Step 4: Check unit status.
        var statusQuery = MessageFactory.CreateStatusQuery("cli-requester", "cli-unit", toType: "unit");
        var statusResult = await unitActor.ReceiveAsync(statusQuery, TestContext.Current.CancellationToken);

        statusResult.ShouldNotBeNull();
        statusResult!.Type.ShouldBe(MessageType.StatusQuery);
        var payload = statusResult.Payload.Deserialize<JsonElement>();
        payload.GetProperty("MemberCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task FullLifecycle_AgentReceivesMessage_StatusReflectsActiveThread()
    {
        // Step 1: Create an agent actor.
        var (agentActor, agentStateManager) = ActorTestHost.CreateAgentActor("cli-agent");

        // Step 2: Send a domain message to the agent.
        var threadId = "cli-conv-1";
        var message = MessageFactory.CreateDomainMessage(threadId: threadId, toId: "cli-agent");
        await agentActor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        // Simulate the state manager now having the active conversation.
        var activeChannel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [message]
        };
        agentStateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveThread, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));

        // Step 3: Query status.
        var statusQuery = MessageFactory.CreateStatusQuery("cli-requester", "cli-agent");
        var statusResult = await agentActor.ReceiveAsync(statusQuery, TestContext.Current.CancellationToken);

        statusResult.ShouldNotBeNull();
        var payload = statusResult!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().ShouldBe("Active");
        payload.GetProperty("ActiveThreadId").GetString().ShouldBe(threadId);
        payload.GetProperty("PendingConversationCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task FullLifecycle_RemoveMember_MemberNoLongerInList()
    {
        var (unitActor, _, _, _) = ActorTestHost.CreateUnitActor(actorId: "rm-unit");
        var agent1 = new Address("agent", TestSlugIds.For("rm-agent-1"));
        var agent2 = new Address("agent", TestSlugIds.For("rm-agent-2"));

        await unitActor.AddMemberAsync(agent1, TestContext.Current.CancellationToken);
        await unitActor.AddMemberAsync(agent2, TestContext.Current.CancellationToken);

        await unitActor.RemoveMemberAsync(agent1, TestContext.Current.CancellationToken);

        var members = await unitActor.GetMembersAsync(TestContext.Current.CancellationToken);
        members.ShouldHaveSingleItem().ShouldBe(agent2);
    }
}
