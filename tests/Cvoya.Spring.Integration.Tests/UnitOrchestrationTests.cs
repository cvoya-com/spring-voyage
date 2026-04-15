// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using NSubstitute;

using Shouldly;

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

        capturedContext.ShouldNotBeNull();
        capturedContext!.Members.Count().ShouldBe(2);
        capturedContext.Members.ShouldContain(member1);
        capturedContext.Members.ShouldContain(member2);
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

        members.Count().ShouldBe(2);
        members.ShouldContain(member1);
        members.ShouldContain(member2);
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

        result.ShouldBe(expectedResponse);
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

        capturedContext.ShouldNotBeNull();
        capturedContext!.UnitAddress.ShouldBe(new Address("unit", "addr-unit"));
    }

    // --- Nested Unit Membership (#98) ---

    [Fact]
    public async Task ReceiveAsync_DomainMessage_UnitMemberReceivesContextContainingUnitAddress()
    {
        // Smoke test for nested dispatch: a unit with a unit-typed member
        // hands the message to its orchestration strategy, which sees the
        // unit member in its context. The strategy that the platform ships
        // with (AiOrchestrationStrategy) emits the dispatch via
        // IUnitContext.SendAsync; here we stub the strategy to capture the
        // selected target so the test stays tied to the seam rather than
        // the AI round-trip.
        var (parent, parentState, parentStrategy) = ActorTestHost.CreateUnitActor(actorId: "parent-unit");
        var agentMember = new Address("agent", "ada");
        var subUnitMember = new Address("unit", "sub-unit");

        parentState.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [agentMember, subUnitMember]));

        Address? chosenTarget = null;
        parentStrategy.OrchestrateAsync(
                Arg.Any<Message>(),
                Arg.Any<IUnitContext>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ctx = callInfo.ArgAt<IUnitContext>(1);
                // Pick the unit-typed member to verify nested dispatch
                // addressability works end-to-end through the unit context.
                chosenTarget = ctx.Members.FirstOrDefault(m =>
                    string.Equals(m.Scheme, "unit", System.StringComparison.OrdinalIgnoreCase));
                return Task.FromResult<Message?>(null);
            });

        var incoming = MessageFactory.CreateDomainMessage(toId: "parent-unit", toType: "unit");

        await parent.ReceiveAsync(incoming, TestContext.Current.CancellationToken);

        chosenTarget.ShouldBe(subUnitMember);
    }

    [Fact]
    public async Task AddMemberAsync_UnitMember_NoCycle_Persists()
    {
        // Directory and proxy factory are wired so cycle detection can
        // resolve the new member and see that it has no sub-members —
        // the add succeeds.
        var directory = Substitute.For<IDirectoryService>();
        var factory = Substitute.For<IActorProxyFactory>();

        var subAddress = new Address("unit", "sub-team");
        directory.ResolveAsync(subAddress, Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                subAddress,
                "sub-actor",
                "sub-team",
                "Sub team",
                null,
                DateTimeOffset.UtcNow));

        var subProxy = Substitute.For<IUnitActor>();
        subProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(System.Array.Empty<Address>());
        factory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == "sub-actor"),
                nameof(UnitActor))
            .Returns(subProxy);

        var (parent, parentState, _) = ActorTestHost.CreateUnitActor(
            actorId: "parent-unit",
            directoryService: directory,
            actorProxyFactory: factory);

        await parent.AddMemberAsync(subAddress, TestContext.Current.CancellationToken);

        await parentState.Received(1).SetStateAsync(
            StateKeys.Members,
            Arg.Is<List<Address>>(list => list.Count == 1 && list[0] == subAddress),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_UnitMember_WouldCreateCycle_ThrowsAndDoesNotPersist()
    {
        // parent-unit tries to add sub-team, but sub-team already contains
        // a unit whose directory entry points back at parent-unit. Reject.
        var directory = Substitute.For<IDirectoryService>();
        var factory = Substitute.For<IActorProxyFactory>();

        var subAddress = new Address("unit", "sub-team");
        directory.ResolveAsync(subAddress, Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                subAddress,
                "sub-actor",
                "sub-team",
                "Sub team",
                null,
                DateTimeOffset.UtcNow));

        var subProxy = Substitute.For<IUnitActor>();
        subProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("unit", "parent-team") });
        factory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == "sub-actor"),
                nameof(UnitActor))
            .Returns(subProxy);

        // "parent-team" resolves back to the "parent-unit" actor.
        directory.ResolveAsync(new Address("unit", "parent-team"), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", "parent-team"),
                "parent-unit",
                "parent-team",
                "Parent team",
                null,
                DateTimeOffset.UtcNow));

        var (parent, parentState, _) = ActorTestHost.CreateUnitActor(
            actorId: "parent-unit",
            directoryService: directory,
            actorProxyFactory: factory);

        await Should.ThrowAsync<CyclicMembershipException>(() =>
            parent.AddMemberAsync(subAddress, TestContext.Current.CancellationToken));

        await parentState.DidNotReceive().SetStateAsync(
            StateKeys.Members,
            Arg.Any<List<Address>>(),
            Arg.Any<CancellationToken>());
    }
}