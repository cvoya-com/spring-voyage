// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using FluentAssertions;

using global::Dapr.Actors.Runtime;

using NSubstitute;

using Xunit;

/// <summary>
/// Integration tests for UnitActor orchestration: strategy dispatch,
/// member management, and context passing.
/// </summary>
public class UnitOrchestrationTests
{
    [Fact]
    public async Task ReceiveAsync_DomainMessage_CallsOrchestrationStrategyWithMessage()
    {
        var (actor, _, strategy) = ActorTestHost.CreateUnitActor(actorId: "orch-unit");

        var message = MessageFactory.CreateDomainMessage(toId: "orch-unit", toType: "unit");
        strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Message?>(null));

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await strategy.Received(1).OrchestrateAsync(
            message,
            Arg.Any<IUnitContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_PassesMembersInContext()
    {
        var (actor, stateManager, strategy) = ActorTestHost.CreateUnitActor(actorId: "orch-unit");
        var member1 = new Address("agent", "agent-1");
        var member2 = new Address("agent", "agent-2");

        stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member1, member2]));

        IUnitContext? capturedContext = null;
        var message = MessageFactory.CreateDomainMessage(toId: "orch-unit", toType: "unit");
        strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedContext = callInfo.ArgAt<IUnitContext>(1);
                return Task.FromResult<Message?>(null);
            });

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        capturedContext.Should().NotBeNull();
        capturedContext!.Members.Should().HaveCount(2);
        capturedContext.Members.Should().Contain(member1);
        capturedContext.Members.Should().Contain(member2);
    }

    [Fact]
    public async Task AddMemberAsync_ThenGetMembers_ReturnsAddedMembers()
    {
        var (actor, stateManager, _) = ActorTestHost.CreateUnitActor(actorId: "member-unit");
        var member1 = new Address("agent", "agent-a");
        var member2 = new Address("agent", "agent-b");

        // Add first member.
        await actor.AddMemberAsync(member1, TestContext.Current.CancellationToken);

        // Simulate state now containing the first member.
        stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member1]));

        // Add second member.
        await actor.AddMemberAsync(member2, TestContext.Current.CancellationToken);

        // Simulate state now containing both members.
        stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member1, member2]));

        var members = await actor.GetMembersAsync(TestContext.Current.CancellationToken);

        members.Should().HaveCount(2);
        members.Should().Contain(member1);
        members.Should().Contain(member2);
    }

    [Fact]
    public async Task ReceiveAsync_StrategyReturnsResponse_ActorReturnsIt()
    {
        var (actor, _, strategy) = ActorTestHost.CreateUnitActor(actorId: "resp-unit");
        var message = MessageFactory.CreateDomainMessage(toId: "resp-unit", toType: "unit");
        var expectedResponse = MessageFactory.CreateDomainMessage(conversationId: message.ConversationId);

        strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(expectedResponse);

        var result = await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().Be(expectedResponse);
    }

    [Fact]
    public async Task ReceiveAsync_UnitContextHasCorrectUnitAddress()
    {
        var (actor, _, strategy) = ActorTestHost.CreateUnitActor(actorId: "addr-unit");

        IUnitContext? capturedContext = null;
        var message = MessageFactory.CreateDomainMessage(toId: "addr-unit", toType: "unit");
        strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedContext = callInfo.ArgAt<IUnitContext>(1);
                return Task.FromResult<Message?>(null);
            });

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        capturedContext.Should().NotBeNull();
        capturedContext!.UnitAddress.Should().Be(new Address("unit", "addr-unit"));
    }
}