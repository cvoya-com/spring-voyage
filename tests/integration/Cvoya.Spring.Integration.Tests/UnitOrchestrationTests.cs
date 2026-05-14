// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for UnitActor runtime dispatch and member management.
/// Per #2052 / ADR-0040 the member graph lives in EF; tests seed the
/// in-memory <see cref="IUnitMemberGraphStore"/> directly.
/// </summary>
public class UnitOrchestrationTests
{
    [Fact]
    public async Task ReceiveAsync_DomainMessage_CallsRuntimeInvocationPathWithMessage()
    {
        var (actor, _, runtimeInvocationPath, _) = ActorTestHost.CreateUnitActor(actorId: "orch-unit");

        var message = MessageFactory.CreateDomainMessage(toId: "orch-unit", toType: "unit");

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("orch-unit")),
            message,
            Arg.Any<CancellationToken>(),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>?>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_WithMembers_StillInvokesRuntimeInvocationPath()
    {
        var (actor, _, runtimeInvocationPath, graph) = ActorTestHost.CreateUnitActor(actorId: "orch-unit");
        graph.SeedAgentMembers(
            TestSlugIds.For("orch-unit"),
            TestSlugIds.For("agent-1"),
            TestSlugIds.For("agent-2"));

        var message = MessageFactory.CreateDomainMessage(toId: "orch-unit", toType: "unit");

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("orch-unit")),
            message,
            Arg.Any<CancellationToken>(),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>?>());
    }

    [Fact]
    public async Task AddMemberAsync_ThenGetMembers_ReturnsAddedMembers()
    {
        var (actor, _, _, _) = ActorTestHost.CreateUnitActor(actorId: "member-unit");
        var member1 = new Address("agent", TestSlugIds.For("agent-a"));
        var member2 = new Address("agent", TestSlugIds.For("agent-b"));

        await actor.AddMemberAsync(member1, TestContext.Current.CancellationToken);
        await actor.AddMemberAsync(member2, TestContext.Current.CancellationToken);

        var members = await actor.GetMembersAsync(TestContext.Current.CancellationToken);

        members.Length.ShouldBe(2);
        members.ShouldContain(member1);
        members.ShouldContain(member2);
    }

    [Fact]
    public async Task ReceiveAsync_RuntimePathCompletes_ActorReturnsNull()
    {
        var (actor, _, _, _) = ActorTestHost.CreateUnitActor(actorId: "resp-unit");
        var message = MessageFactory.CreateDomainMessage(toId: "resp-unit", toType: "unit");

        var result = await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ReceiveAsync_RuntimePathGetsCorrectUnitAddress()
    {
        var (actor, _, runtimeInvocationPath, _) = ActorTestHost.CreateUnitActor(actorId: "addr-unit");

        var message = MessageFactory.CreateDomainMessage(toId: "addr-unit", toType: "unit");

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("addr-unit")),
            message,
            Arg.Any<CancellationToken>(),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>?>());
    }

    // --- Nested Unit Membership (#98 + #2052) ---

    [Fact]
    public async Task ReceiveAsync_DomainMessage_UnitMemberStillInvokesParentRuntime()
    {
        // A unit with a unit-typed child still receives the domain turn as
        // the parent unit. Child delegation is now a runtime/tool decision,
        // not a UnitActor strategy-context decision.
        var (parent, _, runtimeInvocationPath, graph) = ActorTestHost.CreateUnitActor(actorId: "parent-unit");
        graph.SeedAgentMembers(TestSlugIds.For("parent-unit"), TestSlugIds.For("ada"));
        graph.SeedSubunitChildren(TestSlugIds.For("parent-unit"), TestSlugIds.For("sub-unit"));

        var incoming = MessageFactory.CreateDomainMessage(toId: "parent-unit", toType: "unit");

        await parent.ReceiveAsync(incoming, TestContext.Current.CancellationToken);

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("parent-unit")),
            incoming,
            Arg.Any<CancellationToken>(),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>?>());
    }

    [Fact]
    public async Task AddMemberAsync_UnitMember_NoCycle_PersistsEdge()
    {
        // Sub-unit has no children of its own — adding it is safe.
        var subId = new Guid("11111111-2222-3333-4444-555555555555");
        var parentId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var (parent, _, _, graph) = ActorTestHost.CreateUnitActor(
            actorId: parentId.ToString("N"));

        await parent.AddMemberAsync(new Address("unit", subId), TestContext.Current.CancellationToken);

        var children = await graph.ListDirectSubunitChildrenAsync(parentId, TestContext.Current.CancellationToken);
        children.ShouldContain(subId);
    }

    [Fact]
    public async Task AddMemberAsync_UnitMember_WouldCreateCycle_ThrowsAndDoesNotPersist()
    {
        // parent-unit tries to add sub-team, but sub-team already lists
        // parent-unit as one of its children. Reject.
        var subId = new Guid("11111111-2222-3333-4444-555555555555");
        var parentId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var graph = new InMemoryUnitMemberGraphStore();
        graph.SeedSubunitChildren(subId, parentId);

        var (parent, _, _, _) = ActorTestHost.CreateUnitActor(
            actorId: parentId.ToString("N"),
            memberGraphStore: graph);

        await Should.ThrowAsync<CyclicMembershipException>(() =>
            parent.AddMemberAsync(new Address("unit", subId), TestContext.Current.CancellationToken));

        var children = await graph.ListDirectSubunitChildrenAsync(parentId, TestContext.Current.CancellationToken);
        children.ShouldNotContain(subId);
    }
}
