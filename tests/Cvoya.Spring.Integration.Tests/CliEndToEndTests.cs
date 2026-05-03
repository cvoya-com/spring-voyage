// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
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
        // Step 1: Create a unit actor.
        var (unitActor, unitStateManager, strategy) = ActorTestHost.CreateUnitActor(actorId: "cli-unit");

        // Step 2: Add agent members to the unit.
        var agent1 = Address.For("agent", TestSlugIds.HexFor("cli-agent-1"));
        var agent2 = Address.For("agent", TestSlugIds.HexFor("cli-agent-2"));

        await unitActor.AddMemberAsync(agent1, TestContext.Current.CancellationToken);

        // Simulate state after first add.
        unitStateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [agent1]));

        await unitActor.AddMemberAsync(agent2, TestContext.Current.CancellationToken);

        // Simulate state after second add.
        unitStateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [agent1, agent2]));

        // Verify members.
        var members = await unitActor.GetMembersAsync(TestContext.Current.CancellationToken);
        members.Count().ShouldBe(2);

        // Step 3: Send a domain message to the unit.
        var message = MessageFactory.CreateDomainMessage(toId: "cli-unit", toType: "unit");
        strategy.OrchestrateAsync(Arg.Any<Message>(), Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Message?>(null));

        await unitActor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await strategy.Received(1).OrchestrateAsync(
            message,
            Arg.Any<IUnitContext>(),
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
    public async Task FullLifecycle_AgentReceivesMessage_StatusReflectsActiveConversation()
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
        agentStateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
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
        var (unitActor, unitStateManager, _) = ActorTestHost.CreateUnitActor(actorId: "rm-unit");
        var agent1 = Address.For("agent", TestSlugIds.HexFor("rm-agent-1"));
        var agent2 = Address.For("agent", TestSlugIds.HexFor("rm-agent-2"));

        // Add both agents.
        await unitActor.AddMemberAsync(agent1, TestContext.Current.CancellationToken);
        unitStateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [agent1]));

        await unitActor.AddMemberAsync(agent2, TestContext.Current.CancellationToken);
        unitStateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [agent1, agent2]));

        // Remove first agent.
        await unitActor.RemoveMemberAsync(agent1, TestContext.Current.CancellationToken);

        // Simulate state after removal.
        unitStateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [agent2]));

        var members = await unitActor.GetMembersAsync(TestContext.Current.CancellationToken);
        members.ShouldHaveSingleItem().ShouldBe(agent2);
    }
}