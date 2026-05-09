// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Units;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
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
///
/// Post #1629: every actor identifier is the no-dash hex of a Guid; tests
/// build the addresses from named Guid constants.
/// </summary>
public class UnitMembershipCoordinatorTests
{
    private static readonly Guid ParentUnitGuid = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid AgentOneGuid = new("22222222-0000-0000-0000-000000000001");
    private static readonly Guid AgentNewGuid = new("22222222-0000-0000-0000-000000000002");
    private static readonly Guid AgentXGuid = new("22222222-0000-0000-0000-000000000099");
    private static readonly Guid AliasUnitGuid = new("33333333-0000-0000-0000-000000000001");
    private static readonly Guid TeamAGuid = new("44444444-0000-0000-0000-00000000000a");
    private static readonly Guid TeamBGuid = new("44444444-0000-0000-0000-00000000000b");
    private static readonly Guid TeamCGuid = new("44444444-0000-0000-0000-00000000000c");
    private static readonly Guid TeamXGuid = new("55555555-0000-0000-0000-00000000000a");
    private static readonly Guid TeamYGuid = new("55555555-0000-0000-0000-00000000000b");
    private static readonly Guid GhostUnitGuid = new("66666666-0000-0000-0000-000000000001");
    private static readonly Guid FlakyUnitGuid = new("77777777-0000-0000-0000-000000000001");
    private static readonly Guid ChildTeamGuid = new("88888888-0000-0000-0000-000000000001");

    private static readonly string ParentActorId = ParentUnitGuid.ToString("N");
    private static readonly Address ParentAddress = new("unit", ParentUnitGuid);

    private readonly ILogger<UnitMembershipCoordinator> _logger =
        Substitute.For<ILogger<UnitMembershipCoordinator>>();

    private readonly UnitMembershipCoordinator _coordinator;

    public UnitMembershipCoordinatorTests()
    {
        _coordinator = new UnitMembershipCoordinator(
            subunitProjector: null,
            logger: _logger);
    }

    private static DirectoryEntry MakeEntry(Guid actorId, string displayName) =>
        new(new Address("unit", actorId), actorId, displayName, string.Empty, null, DateTimeOffset.UtcNow);

    // --- AddMemberAsync — duplicate detection ---

    [Fact]
    public async Task AddMemberAsync_DuplicateMember_DoesNotCallPersist()
    {
        var member = new Address("agent", AgentOneGuid);
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
        var member = new Address("agent", AgentNewGuid);
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
        // Candidate has a different address but resolves to the same actor
        // id as the parent unit.
        var aliasAddress = new Address("unit", AliasUnitGuid);
        var parentEntry = MakeEntry(ParentUnitGuid, "parent");

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
        var bAddress = new Address("unit", TeamBGuid);
        var bEntry = MakeEntry(TeamBGuid, "team-b");
        var aAliasAddress = new Address("unit", TeamAGuid);
        var aEntry = MakeEntry(ParentUnitGuid, "team-a");
        var bActorId = GuidFormatter.Format(TeamBGuid);

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
                    if (actorId == bActorId)
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
        var cAddress = new Address("unit", TeamCGuid);
        var bAddress = new Address("unit", TeamBGuid);
        var aAliasAddress = new Address("unit", TeamAGuid);
        var cActorId = GuidFormatter.Format(TeamCGuid);
        var bActorId = GuidFormatter.Format(TeamBGuid);

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _coordinator.AddMemberAsync(
                unitActorId: ParentActorId,
                unitAddress: ParentAddress,
                member: cAddress,
                getMembers: _ => Task.FromResult(new List<Address>()),
                persistMembers: (_, _) => Task.CompletedTask,
                resolveAddress: (addr, _) =>
                {
                    if (addr == cAddress) return Task.FromResult<DirectoryEntry?>(MakeEntry(TeamCGuid, "team-c"));
                    if (addr == bAddress) return Task.FromResult<DirectoryEntry?>(MakeEntry(TeamBGuid, "team-b"));
                    if (addr == aAliasAddress) return Task.FromResult<DirectoryEntry?>(MakeEntry(ParentUnitGuid, "team-a"));
                    return Task.FromResult<DirectoryEntry?>(null);
                },
                getSubUnitMembers: (actorId, _) =>
                {
                    if (actorId == cActorId)
                        return Task.FromResult(new[] { bAddress });
                    if (actorId == bActorId)
                        return Task.FromResult(new[] { aAliasAddress });
                    return Task.FromResult(Array.Empty<Address>());
                },
                cancellationToken: TestContext.Current.CancellationToken));

        ex.CyclePath.Count.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task AddMemberAsync_AgentMember_SkipsCycleDetection_NeverCallsResolve()
    {
        var agentAddress = new Address("agent", AgentXGuid);
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
        var ghostAddress = new Address("unit", GhostUnitGuid);

        await _coordinator.AddMemberAsync(
            unitActorId: ParentActorId,
            unitAddress: ParentAddress,
            member: ghostAddress,
            getMembers: _ => Task.FromResult(new List<Address>()),
            persistMembers: (_, _) => Task.CompletedTask,
            resolveAddress: (_, _) => Task.FromResult<DirectoryEntry?>(null),
            getSubUnitMembers: (_, _) => Task.FromResult(Array.Empty<Address>()),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AddMemberAsync_GetSubUnitMembersThrows_TreatsAsDeadEnd_Succeeds()
    {
        var flakyAddress = new Address("unit", FlakyUnitGuid);
        var flakyEntry = MakeEntry(FlakyUnitGuid, "flaky-team");

        await _coordinator.AddMemberAsync(
            unitActorId: ParentActorId,
            unitAddress: ParentAddress,
            member: flakyAddress,
            getMembers: _ => Task.FromResult(new List<Address>()),
            persistMembers: (_, _) => Task.CompletedTask,
            resolveAddress: (_, _) => Task.FromResult<DirectoryEntry?>(flakyEntry),
            getSubUnitMembers: (_, _) => throw new InvalidOperationException("actor unavailable"),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AddMemberAsync_BenignSubGraphCycle_DoesNotFalsePositive()
    {
        // X -> Y -> X (benign side-cycle not involving the parent).
        var xAddress = new Address("unit", TeamXGuid);
        var yAddress = new Address("unit", TeamYGuid);
        var xActorId = GuidFormatter.Format(TeamXGuid);
        var yActorId = GuidFormatter.Format(TeamYGuid);

        await _coordinator.AddMemberAsync(
            unitActorId: ParentActorId,
            unitAddress: ParentAddress,
            member: xAddress,
            getMembers: _ => Task.FromResult(new List<Address>()),
            persistMembers: (_, _) => Task.CompletedTask,
            resolveAddress: (addr, _) =>
            {
                if (addr == xAddress) return Task.FromResult<DirectoryEntry?>(MakeEntry(TeamXGuid, "team-x"));
                if (addr == yAddress) return Task.FromResult<DirectoryEntry?>(MakeEntry(TeamYGuid, "team-y"));
                return Task.FromResult<DirectoryEntry?>(null);
            },
            getSubUnitMembers: (actorId, _) =>
            {
                if (actorId == xActorId) return Task.FromResult(new[] { yAddress });
                if (actorId == yActorId) return Task.FromResult(new[] { xAddress });
                return Task.FromResult(Array.Empty<Address>());
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AddMemberAsync_MaxDepthExceeded_Throws()
    {
        const int chainLength = UnitMembershipCoordinator.MaxCycleDetectionDepth + 2;

        // Build a chain of Guid-addressed nodes.
        var addresses = Enumerable.Range(0, chainLength)
            .Select(i =>
            {
                var bytes = new byte[16];
                BitConverter.GetBytes(i + 1).CopyTo(bytes, 0);
                return new Address("unit", new Guid(bytes));
            })
            .ToArray();

        var actorIds = addresses.Select(a => a.Path).ToArray();

        var entries = addresses
            .Select((addr, i) => MakeEntry(addr.Id, $"node-{i}"))
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
                    var idx = Array.IndexOf(actorIds, actorId);
                    if (idx >= 0 && idx + 1 < chainLength)
                        return Task.FromResult(new[] { addresses[idx + 1] });
                    return Task.FromResult(Array.Empty<Address>());
                },
                cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- RemoveMemberAsync ---

    [Fact]
    public async Task RemoveMemberAsync_ExistingMember_CallsPersistWithSmallerList()
    {
        var member = new Address("agent", AgentOneGuid);
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
        var member = new Address("agent", AgentXGuid);
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

        var subUnit = new Address("unit", ChildTeamGuid);
        var childEntry = MakeEntry(ChildTeamGuid, "child-team");

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
            ParentUnitGuid, ChildTeamGuid, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberAsync_UnitMember_CallsProjectorRemove()
    {
        var projector = Substitute.For<IUnitSubunitMembershipProjector>();
        var coordinator = new UnitMembershipCoordinator(projector, _logger);

        var subUnit = new Address("unit", ChildTeamGuid);
        var existing = new List<Address> { subUnit };

        await coordinator.RemoveMemberAsync(
            unitActorId: ParentActorId,
            member: subUnit,
            getMembers: _ => Task.FromResult(existing),
            persistMembers: (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken);

        await projector.Received(1).ProjectRemoveAsync(
            ParentUnitGuid, ChildTeamGuid, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_AgentMember_DoesNotCallProjector()
    {
        var projector = Substitute.For<IUnitSubunitMembershipProjector>();
        var coordinator = new UnitMembershipCoordinator(projector, _logger);

        var agent = new Address("agent", AgentOneGuid);

        await coordinator.AddMemberAsync(
            unitActorId: ParentActorId,
            unitAddress: ParentAddress,
            member: agent,
            getMembers: _ => Task.FromResult(new List<Address>()),
            persistMembers: (_, _) => Task.CompletedTask,
            resolveAddress: (_, _) => Task.FromResult<DirectoryEntry?>(null),
            getSubUnitMembers: (_, _) => Task.FromResult(Array.Empty<Address>()),
            cancellationToken: TestContext.Current.CancellationToken);

        await projector.DidNotReceiveWithAnyArgs().ProjectAddAsync(default, default, TestContext.Current.CancellationToken);
    }
}
