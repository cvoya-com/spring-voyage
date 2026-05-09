// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
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
/// Integration tests for UnitActor runtime dispatch and member management.
/// </summary>
public class UnitOrchestrationTests
{
    [Fact]
    public async Task ReceiveAsync_DomainMessage_CallsRuntimeInvocationPathWithMessage()
    {
        var (actor, _, runtimeInvocationPath) = ActorTestHost.CreateUnitActor(actorId: "orch-unit");

        var message = MessageFactory.CreateDomainMessage(toId: "orch-unit", toType: "unit");

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("orch-unit")),
            message,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_WithMembers_StillInvokesRuntimeInvocationPath()
    {
        var (actor, stateManager, runtimeInvocationPath) = ActorTestHost.CreateUnitActor(actorId: "orch-unit");
        var member1 = Address.For("agent", TestSlugIds.HexFor("agent-1"));
        var member2 = Address.For("agent", TestSlugIds.HexFor("agent-2"));

        stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member1, member2]));

        var message = MessageFactory.CreateDomainMessage(toId: "orch-unit", toType: "unit");

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("orch-unit")),
            message,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_ThenGetMembers_ReturnsAddedMembers()
    {
        var (actor, stateManager, _) = ActorTestHost.CreateUnitActor(actorId: "member-unit");
        var member1 = Address.For("agent", TestSlugIds.HexFor("agent-a"));
        var member2 = Address.For("agent", TestSlugIds.HexFor("agent-b"));

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
    public async Task ReceiveAsync_RuntimePathCompletes_ActorReturnsNull()
    {
        var (actor, _, _) = ActorTestHost.CreateUnitActor(actorId: "resp-unit");
        var message = MessageFactory.CreateDomainMessage(toId: "resp-unit", toType: "unit");

        var result = await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ReceiveAsync_RuntimePathGetsCorrectUnitAddress()
    {
        var (actor, _, runtimeInvocationPath) = ActorTestHost.CreateUnitActor(actorId: "addr-unit");

        var message = MessageFactory.CreateDomainMessage(toId: "addr-unit", toType: "unit");

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("addr-unit")),
            message,
            Arg.Any<CancellationToken>());
    }

    // --- Nested Unit Membership (#98) ---

    [Fact]
    public async Task ReceiveAsync_DomainMessage_UnitMemberStillInvokesParentRuntime()
    {
        // A unit with a unit-typed child still receives the domain turn as
        // the parent unit. Child delegation is now a runtime/tool decision,
        // not a UnitActor strategy-context decision.
        var (parent, parentState, runtimeInvocationPath) = ActorTestHost.CreateUnitActor(actorId: "parent-unit");
        var agentMember = Address.For("agent", TestSlugIds.HexFor("ada"));
        var subUnitMember = Address.For("unit", TestSlugIds.HexFor("sub-unit"));

        parentState.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [agentMember, subUnitMember]));

        var incoming = MessageFactory.CreateDomainMessage(toId: "parent-unit", toType: "unit");

        await parent.ReceiveAsync(incoming, TestContext.Current.CancellationToken);

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("parent-unit")),
            incoming,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_UnitMember_NoCycle_Persists()
    {
        // Directory and proxy factory are wired so cycle detection can
        // resolve the new member and see that it has no sub-members —
        // the add succeeds.
        var directory = Substitute.For<IDirectoryService>();
        var factory = Substitute.For<IActorProxyFactory>();

        var subId = new Guid("11111111-2222-3333-4444-555555555555");
        var parentId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var subAddress = new Address("unit", subId);
        directory.ResolveAsync(subAddress, Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                subAddress,
                subId,
                "sub-team",
                "Sub team",
                null,
                DateTimeOffset.UtcNow));

        var subProxy = Substitute.For<IUnitActor>();
        subProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(System.Array.Empty<Address>());
        factory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == subId.ToString("N")),
                nameof(UnitActor))
            .Returns(subProxy);

        var (parent, parentState, _) = ActorTestHost.CreateUnitActor(
            actorId: parentId.ToString("N"),
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

        var subId = new Guid("11111111-2222-3333-4444-555555555555");
        var parentId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var subAddress = new Address("unit", subId);
        directory.ResolveAsync(subAddress, Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                subAddress,
                subId,
                "sub-team",
                "Sub team",
                null,
                DateTimeOffset.UtcNow));

        var subProxy = Substitute.For<IUnitActor>();
        var parentAddress = new Address("unit", parentId);
        subProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { parentAddress });
        factory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == subId.ToString("N")),
                nameof(UnitActor))
            .Returns(subProxy);

        // The cycle: subProxy reports parentAddress as a member. That maps
        // back to the parent actor we're calling AddMemberAsync on, so the
        // guard must reject before persisting.
        directory.ResolveAsync(parentAddress, Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                parentAddress,
                parentId,
                "parent-team",
                "Parent team",
                null,
                DateTimeOffset.UtcNow));

        var (parent, parentState, _) = ActorTestHost.CreateUnitActor(
            actorId: parentId.ToString("N"),
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
