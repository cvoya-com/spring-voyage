// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Units;

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
/// edge cases and duplicate handling. Per #2052 / ADR-0040 the
/// coordinator drives <see cref="IUnitMemberGraphStore"/> against EF;
/// these tests use an in-memory fake store so the coordinator's
/// orchestration is the unit under test.
/// </summary>
public class UnitMembershipCoordinatorTests
{
    private static readonly Guid ParentUnitGuid = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid AgentOneGuid = new("22222222-0000-0000-0000-000000000001");
    private static readonly Guid AgentNewGuid = new("22222222-0000-0000-0000-000000000002");
    private static readonly Guid AgentXGuid = new("22222222-0000-0000-0000-000000000099");
    private static readonly Guid TeamBGuid = new("44444444-0000-0000-0000-00000000000b");
    private static readonly Guid TeamCGuid = new("44444444-0000-0000-0000-00000000000c");
    private static readonly Guid TeamXGuid = new("55555555-0000-0000-0000-00000000000a");
    private static readonly Guid TeamYGuid = new("55555555-0000-0000-0000-00000000000b");
    private static readonly Guid GhostUnitGuid = new("66666666-0000-0000-0000-000000000001");
    private static readonly Guid ChildTeamGuid = new("88888888-0000-0000-0000-000000000001");

    private static readonly Address ParentAddress = new("unit", ParentUnitGuid);

    private readonly ILogger<UnitMembershipCoordinator> _logger =
        Substitute.For<ILogger<UnitMembershipCoordinator>>();

    private readonly InMemoryUnitMemberGraphStore _store = new();
    private readonly UnitMembershipCoordinator _coordinator;
    private int _stateChangedCount;

    public UnitMembershipCoordinatorTests()
    {
        _coordinator = new UnitMembershipCoordinator(_store, _logger);
    }

    private Task NoopEmit(Address _, int __, CancellationToken ___)
    {
        _stateChangedCount++;
        return Task.CompletedTask;
    }

    // --- AddMemberAsync — duplicate detection ---

    [Fact]
    public async Task AddMemberAsync_DuplicateAgentMember_DoesNotEmitStateChanged()
    {
        var member = new Address("agent", AgentOneGuid);
        await _store.AddAgentMemberAsync(ParentUnitGuid, AgentOneGuid, TestContext.Current.CancellationToken);
        _stateChangedCount = 0;

        await _coordinator.AddMemberAsync(
            unitId: ParentUnitGuid,
            unitAddress: ParentAddress,
            member: member,
            emitStateChanged: NoopEmit,
            cancellationToken: TestContext.Current.CancellationToken);

        _stateChangedCount.ShouldBe(0);
    }

    [Fact]
    public async Task AddMemberAsync_NewAgentMember_PersistsAndEmits()
    {
        var member = new Address("agent", AgentNewGuid);

        await _coordinator.AddMemberAsync(
            unitId: ParentUnitGuid,
            unitAddress: ParentAddress,
            member: member,
            emitStateChanged: NoopEmit,
            cancellationToken: TestContext.Current.CancellationToken);

        var members = await _store.GetMembersAsync(ParentUnitGuid, TestContext.Current.CancellationToken);
        members.ShouldContain(member);
        _stateChangedCount.ShouldBe(1);
    }

    // --- AddMemberAsync — cycle detection ---

    [Fact]
    public async Task AddMemberAsync_SelfLoop_ByGuid_Throws()
    {
        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _coordinator.AddMemberAsync(
                unitId: ParentUnitGuid,
                unitAddress: ParentAddress,
                member: ParentAddress,
                emitStateChanged: NoopEmit,
                cancellationToken: TestContext.Current.CancellationToken));

        ex.ParentUnit.ShouldBe(ParentAddress);
        ex.CandidateMember.ShouldBe(ParentAddress);
        ex.CyclePath.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AddMemberAsync_TwoCycle_Throws()
    {
        // B already contains A. Adding B to A must be rejected.
        var bAddress = new Address("unit", TeamBGuid);
        await _store.AddSubunitMemberAsync(TeamBGuid, ParentUnitGuid, TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _coordinator.AddMemberAsync(
                unitId: ParentUnitGuid,
                unitAddress: ParentAddress,
                member: bAddress,
                emitStateChanged: NoopEmit,
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
        await _store.AddSubunitMemberAsync(TeamCGuid, TeamBGuid, TestContext.Current.CancellationToken);
        await _store.AddSubunitMemberAsync(TeamBGuid, ParentUnitGuid, TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _coordinator.AddMemberAsync(
                unitId: ParentUnitGuid,
                unitAddress: ParentAddress,
                member: cAddress,
                emitStateChanged: NoopEmit,
                cancellationToken: TestContext.Current.CancellationToken));

        ex.CyclePath.Count.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task AddMemberAsync_AgentMember_SkipsCycleDetection()
    {
        // Agents are leaves — the coordinator must not query the
        // sub-unit projection at all for agent-typed members.
        var agentAddress = new Address("agent", AgentXGuid);

        await _coordinator.AddMemberAsync(
            unitId: ParentUnitGuid,
            unitAddress: ParentAddress,
            member: agentAddress,
            emitStateChanged: NoopEmit,
            cancellationToken: TestContext.Current.CancellationToken);

        _store.ListDirectSubunitChildrenCalls.ShouldBe(0);
    }

    [Fact]
    public async Task AddMemberAsync_UnknownSubUnit_TreatsAsDeadEnd_Succeeds()
    {
        var ghostAddress = new Address("unit", GhostUnitGuid);

        await _coordinator.AddMemberAsync(
            unitId: ParentUnitGuid,
            unitAddress: ParentAddress,
            member: ghostAddress,
            emitStateChanged: NoopEmit,
            cancellationToken: TestContext.Current.CancellationToken);

        var members = await _store.GetMembersAsync(ParentUnitGuid, TestContext.Current.CancellationToken);
        members.ShouldContain(ghostAddress);
    }

    [Fact]
    public async Task AddMemberAsync_BenignSubGraphCycle_DoesNotFalsePositive()
    {
        // X -> Y -> X (benign side-cycle not involving the parent).
        var xAddress = new Address("unit", TeamXGuid);
        await _store.AddSubunitMemberAsync(TeamXGuid, TeamYGuid, TestContext.Current.CancellationToken);
        await _store.AddSubunitMemberAsync(TeamYGuid, TeamXGuid, TestContext.Current.CancellationToken);

        await _coordinator.AddMemberAsync(
            unitId: ParentUnitGuid,
            unitAddress: ParentAddress,
            member: xAddress,
            emitStateChanged: NoopEmit,
            cancellationToken: TestContext.Current.CancellationToken);

        var members = await _store.GetMembersAsync(ParentUnitGuid, TestContext.Current.CancellationToken);
        members.ShouldContain(xAddress);
    }

    [Fact]
    public async Task AddMemberAsync_MaxDepthExceeded_Throws()
    {
        const int chainLength = UnitMembershipCoordinator.MaxCycleDetectionDepth + 2;

        // Build a non-cyclic chain so the only termination is the depth bound.
        var ids = Enumerable.Range(0, chainLength)
            .Select(i =>
            {
                var bytes = new byte[16];
                BitConverter.GetBytes(i + 1).CopyTo(bytes, 0);
                return new Guid(bytes);
            })
            .ToArray();

        for (var i = 0; i < chainLength - 1; i++)
        {
            await _store.AddSubunitMemberAsync(ids[i], ids[i + 1], TestContext.Current.CancellationToken);
        }

        var headAddress = new Address("unit", ids[0]);

        await Should.ThrowAsync<CyclicMembershipException>(() =>
            _coordinator.AddMemberAsync(
                unitId: ParentUnitGuid,
                unitAddress: ParentAddress,
                member: headAddress,
                emitStateChanged: NoopEmit,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- RemoveMemberAsync ---

    [Fact]
    public async Task RemoveMemberAsync_ExistingAgentMember_RemovesAndEmits()
    {
        var member = new Address("agent", AgentOneGuid);
        await _store.AddAgentMemberAsync(ParentUnitGuid, AgentOneGuid, TestContext.Current.CancellationToken);
        _stateChangedCount = 0;

        await _coordinator.RemoveMemberAsync(
            unitId: ParentUnitGuid,
            member: member,
            emitStateChanged: NoopEmit,
            cancellationToken: TestContext.Current.CancellationToken);

        var members = await _store.GetMembersAsync(ParentUnitGuid, TestContext.Current.CancellationToken);
        members.ShouldNotContain(member);
        _stateChangedCount.ShouldBe(1);
    }

    [Fact]
    public async Task RemoveMemberAsync_NonExistentMember_DoesNotEmit()
    {
        var member = new Address("agent", AgentXGuid);

        await _coordinator.RemoveMemberAsync(
            unitId: ParentUnitGuid,
            member: member,
            emitStateChanged: NoopEmit,
            cancellationToken: TestContext.Current.CancellationToken);

        _stateChangedCount.ShouldBe(0);
    }

    // --- Sub-unit edge writes ---

    [Fact]
    public async Task AddMemberAsync_UnitMember_WritesSubunitEdge()
    {
        var subUnit = new Address("unit", ChildTeamGuid);

        await _coordinator.AddMemberAsync(
            unitId: ParentUnitGuid,
            unitAddress: ParentAddress,
            member: subUnit,
            emitStateChanged: NoopEmit,
            cancellationToken: TestContext.Current.CancellationToken);

        var children = await _store.ListDirectSubunitChildrenAsync(ParentUnitGuid, TestContext.Current.CancellationToken);
        children.ShouldContain(ChildTeamGuid);
    }

    [Fact]
    public async Task RemoveMemberAsync_UnitMember_RemovesSubunitEdge()
    {
        var subUnit = new Address("unit", ChildTeamGuid);
        await _store.AddSubunitMemberAsync(ParentUnitGuid, ChildTeamGuid, TestContext.Current.CancellationToken);

        await _coordinator.RemoveMemberAsync(
            unitId: ParentUnitGuid,
            member: subUnit,
            emitStateChanged: NoopEmit,
            cancellationToken: TestContext.Current.CancellationToken);

        var children = await _store.ListDirectSubunitChildrenAsync(ParentUnitGuid, TestContext.Current.CancellationToken);
        children.ShouldNotContain(ChildTeamGuid);
    }

    /// <summary>
    /// In-memory fake of <see cref="IUnitMemberGraphStore"/> used to drive
    /// the coordinator under test against deterministic graph state without
    /// spinning a real <see cref="Cvoya.Spring.Dapr.Data.SpringDbContext"/>.
    /// </summary>
    private sealed class InMemoryUnitMemberGraphStore : IUnitMemberGraphStore
    {
        private readonly Dictionary<Guid, HashSet<Guid>> _agentMembers = [];
        private readonly Dictionary<Guid, HashSet<Guid>> _subunitChildren = [];

        public int ListDirectSubunitChildrenCalls { get; private set; }

        public Task<IReadOnlyList<Address>> GetMembersAsync(
            Guid unitId, CancellationToken cancellationToken = default)
        {
            var members = new List<Address>();
            if (_agentMembers.TryGetValue(unitId, out var agents))
            {
                foreach (var a in agents.OrderBy(g => g))
                {
                    members.Add(new Address("agent", a));
                }
            }
            if (_subunitChildren.TryGetValue(unitId, out var subs))
            {
                foreach (var c in subs.OrderBy(g => g))
                {
                    members.Add(new Address("unit", c));
                }
            }
            return Task.FromResult<IReadOnlyList<Address>>(members);
        }

        public Task<bool> AddAgentMemberAsync(
            Guid unitId, Guid agentId, CancellationToken cancellationToken = default)
        {
            if (!_agentMembers.TryGetValue(unitId, out var set))
            {
                set = [];
                _agentMembers[unitId] = set;
            }
            return Task.FromResult(set.Add(agentId));
        }

        public Task<bool> AddSubunitMemberAsync(
            Guid parentId, Guid childId, CancellationToken cancellationToken = default)
        {
            if (!_subunitChildren.TryGetValue(parentId, out var set))
            {
                set = [];
                _subunitChildren[parentId] = set;
            }
            return Task.FromResult(set.Add(childId));
        }

        public Task<bool> RemoveAgentMemberAsync(
            Guid unitId, Guid agentId, CancellationToken cancellationToken = default)
            => Task.FromResult(
                _agentMembers.TryGetValue(unitId, out var set) && set.Remove(agentId));

        public Task<bool> RemoveSubunitMemberAsync(
            Guid parentId, Guid childId, CancellationToken cancellationToken = default)
            => Task.FromResult(
                _subunitChildren.TryGetValue(parentId, out var set) && set.Remove(childId));

        public Task<IReadOnlyList<Guid>> ListDirectSubunitChildrenAsync(
            Guid parentId, CancellationToken cancellationToken = default)
        {
            ListDirectSubunitChildrenCalls++;
            if (_subunitChildren.TryGetValue(parentId, out var set))
            {
                return Task.FromResult<IReadOnlyList<Guid>>(set.OrderBy(g => g).ToList());
            }
            return Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
        }

        public Task EnsureTopLevelEdgeAsync(
            Guid unitId, Guid tenantId, CancellationToken cancellationToken = default)
        {
            return AddSubunitMemberAsync(tenantId, unitId, cancellationToken);
        }
    }
}
