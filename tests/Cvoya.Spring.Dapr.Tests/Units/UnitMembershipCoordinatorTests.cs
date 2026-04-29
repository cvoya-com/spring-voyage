// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Units;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Units;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitMembershipCoordinator"/> exercised directly
/// (without going through <c>UnitActor</c>) to validate cycle-detection
/// edge cases and duplicate handling in isolation.
/// </summary>
public class UnitMembershipCoordinatorTests
{
    private const string ParentActorId = "parent-unit";
    private static readonly Address ParentAddress = new("unit", ParentActorId);

    private readonly ILogger<UnitMembershipCoordinator> _logger =
        Substitute.For<ILogger<UnitMembershipCoordinator>>();

    private readonly UnitMembershipCoordinator _coordinator;

    public UnitMembershipCoordinatorTests()
    {
        _coordinator = new UnitMembershipCoordinator(
            subunitProjector: null,
            logger: _logger);
    }

    // --- AddMemberAsync — duplicate detection ---

    [Fact]
    public async Task AddMemberAsync_DuplicateMember_DoesNotCallPersist()
    {
        var member = new Address("agent", "agent-1");
        var existing = new List<Address> { member };
        var persistCalled = false;

        await _coordinator.AddMemberAsync(
            unitActorId: ParentActorId,
            unitAddress: ParentAddress,
            member: member,
            getMembers: _ => Task.FromResult(existing),
            persistMembers: (_, _) => { persistCalled = true; return Task.CompletedTask; },
            resolveAddress: (_, _) => Task.FromResult<DirectoryEntry?>(null),
            getSubUnitMembers: (_, _) => Task.FromResult(Array.Empty<Address>()),
            cancellationToken: TestContext.Current.CancellationToken);

        persistCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task AddMemberAsync_NewMember_CallsPersistWithUpdatedList()
    {
        var member = new Address("agent", "agent-new");
        var existing = new List<Address>();
        List<Address>? persisted = null;

        await _coordinator.AddMemberAsync(
            unitActorId: ParentActorId,
            unitAddress: ParentAddress,
            member: member,
            getMembers: _ => Task.FromResult(existing),
            persistMembers: (list, _) => { persisted = list; return Task.CompletedTask; },
            resolveAddress: (_, _) => Task.FromResult<DirectoryEntry?>(null),
            getSubUnitMembers: (_, _) => Task.FromResult(Array.Empty<Address>()),
            cancellationToken: TestContext.Current.CancellationToken);

        persisted.ShouldNotBeNull();
        persisted!.ShouldContain(member);
    }

    // --- AddMemberAsync — cycle detection ---

    [Fact]
    public async Task AddMemberAsync_SelfLoop_ByAddress_Throws()
    {
        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _coordinator.AddMemberAsync(
                unitActorId: ParentActorId,
                unitAddress: ParentAddress,
                member: ParentAddress,
                getMembers: _ => Task.FromResult(new List<Address>()),
                persistMembers: (_, _) => Task.CompletedTask,
                resolveAddress: (_, _) => Task.FromResult<DirectoryEntry?>(null),
                getSubUnitMembers: (_, _) => Task.FromResult(Array.Empty<Address>()),
                cancellationToken: TestContext.Current.CancellationToken));

        ex.ParentUnit.ShouldBe(ParentAddress);
        ex.CandidateMember.ShouldBe(ParentAddress);
        ex.CyclePath.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AddMemberAsync_SelfLoop_ViaDirectoryResolution_Throws()
    {
        // Candidate has a different address form ("my-team") but resolves
        // to the same actor id as the parent unit.
        var aliasAddress = new Address("unit", "my-team");
        var parentEntry = MakeEntry("my-team", ParentActorId);

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _coordinator.AddMemberAsync(
                unitActorId: ParentActorId,
                unitAddress: ParentAddress,
                member: aliasAddress,
                getMembers: _ => Task.FromResult(new List<Address>()),
                persistMembers: (_, _) => Task.CompletedTask,
                resolveAddress: (addr, _) => Task.FromResult<DirectoryEntry?>(
                    addr == aliasAddress ? parentEntry : null),
                getSubUnitMembers: (_, _) => Task.FromResult(Array.Empty<Address>()),
                cancellationToken: TestContext.Current.CancellationToken));

        ex.CandidateMember.ShouldBe(aliasAddress);
    }

    [Fact]
    public async Task AddMemberAsync_TwoCycle_Throws()
    {
        // B already contains A. Adding B to A must be rejected.
        var bAddress = new Address("unit", "team-b");
        var bEntry = MakeEntry("team-b", "b-actor");
        var aAliasAddress = new Address("unit", "team-a");
        var aEntry = MakeEntry("team-a", ParentActorId);

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _coordinator.AddMemberAsync(
                unitActorId: ParentActorId,
                unitAddress: ParentAddress,
                member: bAddress,
                getMembers: _ => Task.FromResult(new List<Address>()),
                persistMembers: (_, _) => Task.CompletedTask,
                resolveAddress: (addr, _) =>
                {
                    if (addr == bAddress) return Task.FromResult<DirectoryEntry?>(bEntry);
                    if (addr == aAliasAddress) return Task.FromResult<DirectoryEntry?>(aEntry);
                    return Task.FromResult<DirectoryEntry?>(null);
                },
                getSubUnitMembers: (actorId, _) =>
                {
                    if (actorId == "b-actor")
                        return Task.FromResult(new[] { aAliasAddress });
                    return Task.FromResult(Array.Empty<Address>());
                },
                cancellationToken: TestContext.Current.CancellationToken));

        ex.CandidateMember.ShouldBe(bAddress);
        ex.Message.ShouldContain("cycle");
        ex.CyclePath.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task AddMemberAsync_DeepCycle_ThreeLevels_Throws()
    {
        // C -> B -> A (A is the parent). Adding C to A must be rejected.
        var cAddress = new Address("unit", "team-c");
        var bAddress = new Address("unit", "team-b");
        var aAliasAddress = new Address("unit", "team-a");

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _coordinator.AddMemberAsync(
                unitActorId: ParentActorId,
                unitAddress: ParentAddress,
                member: cAddress,
                getMembers: _ => Task.FromResult(new List<Address>()),
                persistMembers: (_, _) => Task.CompletedTask,
                resolveAddress: (addr, _) =>
                {
                    if (addr == cAddress) return Task.FromResult<DirectoryEntry?>(MakeEntry("team-c", "c-actor"));
                    if (addr == bAddress) return Task.FromResult<DirectoryEntry?>(MakeEntry("team-b", "b-actor"));
                    if (addr == aAliasAddress) return Task.FromResult<DirectoryEntry?>(MakeEntry("team-a", ParentActorId));
                    return Task.FromResult<DirectoryEntry?>(null);
                },
                getSubUnitMembers: (actorId, _) =>
                {
                    if (actorId == "c-actor")
                        return Task.FromResult(new[] { bAddress });
                    if (actorId == "b-actor")
                        return Task.FromResult(new[] { aAliasAddress });
                    return Task.FromResult(Array.Empty<Address>());
                },
                cancellationToken: TestContext.Current.CancellationToken));

        ex.CyclePath.Count.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task AddMemberAsync_AgentMember_SkipsCycleDetection_NeverCallsResolve()
    {
        // Agents are leaves — resolveAddress must never be called.
        var agentAddress = new Address("agent", "agent-x");
        var resolveCalled = false;

        await _coordinator.AddMemberAsync(
            unitActorId: ParentActorId,
            unitAddress: ParentAddress,
            member: agentAddress,
            getMembers: _ => Task.FromResult(new List<Address>()),
            persistMembers: (_, _) => Task.CompletedTask,
            resolveAddress: (_, _) => { resolveCalled = true; return Task.FromResult<DirectoryEntry?>(null); },
            getSubUnitMembers: (_, _) => Task.FromResult(Array.Empty<Address>()),
            cancellationToken: TestContext.Current.CancellationToken);

        resolveCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task AddMemberAsync_UnknownSubUnit_TreatsAsDeadEnd_Succeeds()
    {
        // The directory returns null for the candidate (deleted unit). Should
        // not block the add.
        var ghostAddress = new Address("unit", "ghost-team");

        await _coordinator.AddMemberAsync(
            unitActorId: ParentActorId,
            unitAddress: ParentAddress,
            member: ghostAddress,
            getMembers: _ => Task.FromResult(new List<Address>()),
            persistMembers: (_, _) => Task.CompletedTask,
            resolveAddress: (_, _) => Task.FromResult<DirectoryEntry?>(null),
            getSubUnitMembers: (_, _) => Task.FromResult(Array.Empty<Address>()),
            cancellationToken: TestContext.Current.CancellationToken);

        // No throw — success is the assertion.
    }

    [Fact]
    public async Task AddMemberAsync_GetSubUnitMembersThrows_TreatsAsDeadEnd_Succeeds()
    {
        // Actor unreachable mid-walk. Should not block the add.
        var flakyAddress = new Address("unit", "flaky-team");
        var flakyEntry = MakeEntry("flaky-team", "flaky-actor");

        await _coordinator.AddMemberAsync(
            unitActorId: ParentActorId,
            unitAddress: ParentAddress,
            member: flakyAddress,
            getMembers: _ => Task.FromResult(new List<Address>()),
            persistMembers: (_, _) => Task.CompletedTask,
            resolveAddress: (_, _) => Task.FromResult<DirectoryEntry?>(flakyEntry),
            getSubUnitMembers: (_, _) => throw new InvalidOperationException("actor unavailable"),
            cancellationToken: TestContext.Current.CancellationToken);

        // No throw — success is the assertion.
    }

    [Fact]
    public async Task AddMemberAsync_BenignSubGraphCycle_DoesNotFalsePositive()
    {
        // X -> Y -> X (benign side-cycle not involving the parent). Must not
        // block adding X to the parent.
        var xAddress = new Address("unit", "team-x");
        var yAddress = new Address("unit", "team-y");

        await _coordinator.AddMemberAsync(
            unitActorId: ParentActorId,
            unitAddress: ParentAddress,
            member: xAddress,
            getMembers: _ => Task.FromResult(new List<Address>()),
            persistMembers: (_, _) => Task.CompletedTask,
            resolveAddress: (addr, _) =>
            {
                if (addr == xAddress) return Task.FromResult<DirectoryEntry?>(MakeEntry("team-x", "x-actor"));
                if (addr == yAddress) return Task.FromResult<DirectoryEntry?>(MakeEntry("team-y", "y-actor"));
                return Task.FromResult<DirectoryEntry?>(null);
            },
            getSubUnitMembers: (actorId, _) =>
            {
                if (actorId == "x-actor") return Task.FromResult(new[] { yAddress });
                if (actorId == "y-actor") return Task.FromResult(new[] { xAddress });
                return Task.FromResult(Array.Empty<Address>());
            },
            cancellationToken: TestContext.Current.CancellationToken);

        // No throw — success is the assertion.
    }

    [Fact]
    public async Task AddMemberAsync_MaxDepthExceeded_Throws()
    {
        // Build a chain of length > MaxCycleDetectionDepth that does NOT
        // loop back to the parent. The depth bound must still reject it.
        const int chainLength = UnitMembershipCoordinator.MaxCycleDetectionDepth + 2;

        // Create addresses and entries for each node in the chain.
        var addresses = Enumerable.Range(0, chainLength)
            .Select(i => new Address("unit", $"node-{i}"))
            .ToArray();

        // Each node points to the next (linear chain — no cycle back to parent).
        var entries = addresses
            .Select((addr, i) => MakeEntry(addr.Path, $"actor-{i}"))
            .ToArray();

        await Should.ThrowAsync<CyclicMembershipException>(() =>
            _coordinator.AddMemberAsync(
                unitActorId: ParentActorId,
                unitAddress: ParentAddress,
                member: addresses[0],
                getMembers: _ => Task.FromResult(new List<Address>()),
                persistMembers: (_, _) => Task.CompletedTask,
                resolveAddress: (addr, _) =>
                {
                    var idx = Array.FindIndex(addresses, a => a == addr);
                    return Task.FromResult<DirectoryEntry?>(idx >= 0 ? entries[idx] : null);
                },
                getSubUnitMembers: (actorId, _) =>
                {
                    var idx = int.Parse(actorId.Replace("actor-", ""));
                    if (idx + 1 < chainLength)
                        return Task.FromResult(new[] { addresses[idx + 1] });
                    return Task.FromResult(Array.Empty<Address>());
                },
                cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- RemoveMemberAsync ---

    [Fact]
    public async Task RemoveMemberAsync_ExistingMember_CallsPersistWithSmallerList()
    {
        var member = new Address("agent", "agent-1");
        var existing = new List<Address> { member };
        List<Address>? persisted = null;

        await _coordinator.RemoveMemberAsync(
            unitActorId: ParentActorId,
            member: member,
            getMembers: _ => Task.FromResult(existing),
            persistMembers: (list, _) => { persisted = list; return Task.CompletedTask; },
            cancellationToken: TestContext.Current.CancellationToken);

        persisted.ShouldNotBeNull();
        persisted!.ShouldNotContain(member);
    }

    [Fact]
    public async Task RemoveMemberAsync_NonExistentMember_DoesNotCallPersist()
    {
        var member = new Address("agent", "ghost");
        var existing = new List<Address>();
        var persistCalled = false;

        await _coordinator.RemoveMemberAsync(
            unitActorId: ParentActorId,
            member: member,
            getMembers: _ => Task.FromResult(existing),
            persistMembers: (_, _) => { persistCalled = true; return Task.CompletedTask; },
            cancellationToken: TestContext.Current.CancellationToken);

        persistCalled.ShouldBeFalse();
    }

    // --- SubunitProjector integration ---

    [Fact]
    public async Task AddMemberAsync_UnitMember_CallsProjectorAdd()
    {
        var projector = Substitute.For<IUnitSubunitMembershipProjector>();
        var coordinator = new UnitMembershipCoordinator(projector, _logger);

        var subUnit = new Address("unit", "child-team");
        var childEntry = MakeEntry("child-team", "child-actor");

        await coordinator.AddMemberAsync(
            unitActorId: ParentActorId,
            unitAddress: ParentAddress,
            member: subUnit,
            getMembers: _ => Task.FromResult(new List<Address>()),
            persistMembers: (_, _) => Task.CompletedTask,
            resolveAddress: (_, _) => Task.FromResult<DirectoryEntry?>(childEntry),
            getSubUnitMembers: (_, _) => Task.FromResult(Array.Empty<Address>()),
            cancellationToken: TestContext.Current.CancellationToken);

        await projector.Received(1).ProjectAddAsync(
            ParentActorId, subUnit.Path, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberAsync_UnitMember_CallsProjectorRemove()
    {
        var projector = Substitute.For<IUnitSubunitMembershipProjector>();
        var coordinator = new UnitMembershipCoordinator(projector, _logger);

        var subUnit = new Address("unit", "child-team");
        var existing = new List<Address> { subUnit };

        await coordinator.RemoveMemberAsync(
            unitActorId: ParentActorId,
            member: subUnit,
            getMembers: _ => Task.FromResult(existing),
            persistMembers: (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken);

        await projector.Received(1).ProjectRemoveAsync(
            ParentActorId, subUnit.Path, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_AgentMember_DoesNotCallProjector()
    {
        var projector = Substitute.For<IUnitSubunitMembershipProjector>();
        var coordinator = new UnitMembershipCoordinator(projector, _logger);

        var agent = new Address("agent", "agent-1");

        await coordinator.AddMemberAsync(
            unitActorId: ParentActorId,
            unitAddress: ParentAddress,
            member: agent,
            getMembers: _ => Task.FromResult(new List<Address>()),
            persistMembers: (_, _) => Task.CompletedTask,
            resolveAddress: (_, _) => Task.FromResult<DirectoryEntry?>(null),
            getSubUnitMembers: (_, _) => Task.FromResult(Array.Empty<Address>()),
            cancellationToken: TestContext.Current.CancellationToken);

        await projector.DidNotReceive().ProjectAddAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static DirectoryEntry MakeEntry(string path, string actorId) =>
        new(
            new Address("unit", path),
            actorId,
            path,
            $"Unit {path}",
            null,
            DateTimeOffset.UtcNow);
}