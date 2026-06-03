// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Units;

using System;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Dapr.Units;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="HatReachabilityService"/> — the v0.1 Hat ↔ unit
/// reachability rule (#2972, ADR-0062 § 11). Pins every example from the
/// issue's authoritative comment:
///
/// <code>
///   UnitA ─┬─ HumanD (human member)
///          ├─ AgentX (agent member)
///          └─ SubUnitB (sub-unit member) ─┬─ HumanC (human member)
///                                         └─ AgentY (agent member)
///   OtherUnit  (no human member)
/// </code>
///
/// HumanD reaches UnitA + UnitA's direct members (AgentX, SubUnitB) but not
/// into SubUnitB (AgentY, HumanC). HumanC reaches SubUnitB + AgentY but not
/// the parent UnitA.
/// </summary>
public class HatReachabilityServiceTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-2222-2222-2222-000000000099");
    private static readonly Guid Operator = OssTenantUserIds.Operator;

    private static readonly Guid UnitA = Guid.Parse("11111111-0000-0000-0000-0000000000a1");
    private static readonly Guid SubUnitB = Guid.Parse("11111111-0000-0000-0000-0000000000b2");
    private static readonly Guid OtherUnit = Guid.Parse("11111111-0000-0000-0000-0000000000c3");
    private static readonly Guid AgentX = Guid.Parse("22222222-0000-0000-0000-0000000000a1");
    private static readonly Guid AgentY = Guid.Parse("22222222-0000-0000-0000-0000000000b2");

    private readonly ServiceProvider _provider;

    // Populated by SeedTopologyAsync.
    private Guid _humanD;
    private Guid _humanC;
    private Guid _orphanHat;

    public HatReachabilityServiceTests()
    {
        // Capture the in-memory database name ONCE so every scope's
        // DbContext shares the same store — the seed written in one scope
        // must be visible to the service resolved in another.
        var dbName = $"reach-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantId));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(dbName));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── ReachesAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReachesAsync_DirectHumanMember_ReachesOwnUnit()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        (await svc.ReachesAsync(_humanD, Unit(UnitA), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task ReachesAsync_HatReachesSiblingAgent()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        (await svc.ReachesAsync(_humanD, Agent(AgentX), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task ReachesAsync_HatReachesSiblingSubUnit()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        (await svc.ReachesAsync(_humanD, Unit(SubUnitB), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task ReachesAsync_HatDoesNotReachIntoSubUnit()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        // HumanD reaches SubUnitB the unit, but not SubUnitB's own members.
        (await svc.ReachesAsync(_humanD, Agent(AgentY), Ct)).ShouldBeFalse();
        (await svc.ReachesAsync(_humanD, Human(_humanC), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task ReachesAsync_SubUnitHatDoesNotReachParent()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        // UnitA → SubUnitB → HumanC: HumanC cannot reach UnitA.
        (await svc.ReachesAsync(_humanC, Unit(UnitA), Ct)).ShouldBeFalse();
        (await svc.ReachesAsync(_humanC, Agent(AgentX), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task ReachesAsync_SubUnitHatReachesOwnUnitAndMembers()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        (await svc.ReachesAsync(_humanC, Unit(SubUnitB), Ct)).ShouldBeTrue();
        (await svc.ReachesAsync(_humanC, Agent(AgentY), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task ReachesAsync_OrphanHat_ReachesNothing()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        (await svc.ReachesAsync(_orphanHat, Unit(UnitA), Ct)).ShouldBeFalse();
        (await svc.ReachesAsync(_orphanHat, Unit(SubUnitB), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task ReachesAsync_NonRoutableScheme_False()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        (await svc.ReachesAsync(_humanD, new Address(Address.ConnectorScheme, Guid.NewGuid()), Ct))
            .ShouldBeFalse();
    }

    // ── GetWearableHatsAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetWearableHatsAsync_TargetUnitA_OffersOnlyTheUnitAHat()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        var wearable = await svc.GetWearableHatsAsync(Operator, new[] { Unit(UnitA) }, Ct);
        wearable.ShouldBe(new[] { _humanD }, ignoreOrder: true);
    }

    [Fact]
    public async Task GetWearableHatsAsync_TargetSubUnit_OffersOwnAndParentHat()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        // Both HumanC (own unit) and HumanD (parent's hat reaching the
        // sibling sub-unit) can message SubUnitB.
        var wearable = await svc.GetWearableHatsAsync(Operator, new[] { Unit(SubUnitB) }, Ct);
        wearable.ShouldBe(new[] { _humanC, _humanD }, ignoreOrder: true);
    }

    [Fact]
    public async Task GetWearableHatsAsync_TargetAgentInSubUnit_OffersOnlySubUnitHat()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        var wearable = await svc.GetWearableHatsAsync(Operator, new[] { Agent(AgentY) }, Ct);
        wearable.ShouldBe(new[] { _humanC }, ignoreOrder: true);
    }

    [Fact]
    public async Task GetWearableHatsAsync_TargetWithNoHumanMember_IsEmpty()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        // OtherUnit has no human member and is no one's reachable sibling →
        // the gate: the operator wears no Hat that can message it.
        var wearable = await svc.GetWearableHatsAsync(Operator, new[] { Unit(OtherUnit) }, Ct);
        wearable.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetWearableHatsAsync_MultiTarget_IntersectsReachability()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        // UnitA + SubUnitB: only HumanD reaches both (UnitA as own unit,
        // SubUnitB as a sibling sub-unit). HumanC reaches SubUnitB but not
        // UnitA, so it is excluded by the intersection.
        var wearable = await svc.GetWearableHatsAsync(
            Operator, new[] { Unit(UnitA), Unit(SubUnitB) }, Ct);
        wearable.ShouldBe(new[] { _humanD }, ignoreOrder: true);
    }

    [Fact]
    public async Task GetWearableHatsAsync_EmptyTargets_ReturnsAllBoundHats()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        var wearable = await svc.GetWearableHatsAsync(Operator, Array.Empty<Address>(), Ct);
        wearable.ShouldBe(new[] { _humanD, _humanC, _orphanHat }, ignoreOrder: true);
    }

    [Fact]
    public async Task GetWearableHatsAsync_NoBoundHats_IsEmpty()
    {
        await SeedTopologyAsync();
        var svc = CreateService();
        var stranger = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
        var wearable = await svc.GetWearableHatsAsync(stranger, new[] { Unit(UnitA) }, Ct);
        wearable.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetWearableHatsAsync_EmptyCaller_Throws()
    {
        var svc = CreateService();
        await Should.ThrowAsync<ArgumentException>(() =>
            svc.GetWearableHatsAsync(Guid.Empty, new[] { Unit(UnitA) }, Ct));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Address Unit(Guid id) => new(Address.UnitScheme, id);
    private static Address Agent(Guid id) => new(Address.AgentScheme, id);
    private static Address Human(Guid id) => new(Address.HumanScheme, id);

    private HatReachabilityService CreateService()
    {
        var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        return new HatReachabilityService(db);
    }

    private async Task SeedTopologyAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        db.TenantUsers.Add(new TenantUserEntity
        {
            Id = Operator,
            TenantId = TenantId,
            DisplayName = "Operator",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        _humanD = NewHat(db, "HumanD");
        _humanC = NewHat(db, "HumanC");
        _orphanHat = NewHat(db, "Orphan");

        // Human memberships (the anchors).
        AddHumanMembership(db, UnitA, _humanD);
        AddHumanMembership(db, SubUnitB, _humanC);
        // _orphanHat deliberately has no membership.

        // Agent memberships.
        AddAgentMembership(db, UnitA, AgentX);
        AddAgentMembership(db, SubUnitB, AgentY);

        // Sub-unit edge: UnitA contains SubUnitB.
        db.UnitSubunitMemberships.Add(new UnitSubunitMembershipEntity
        {
            TenantId = TenantId,
            ParentId = UnitA,
            ChildId = SubUnitB,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(Ct);
    }

    private static Guid NewHat(SpringDbContext db, string name)
    {
        var id = Guid.NewGuid();
        db.Humans.Add(new HumanEntity
        {
            Id = id,
            TenantId = TenantId,
            TenantUserId = Operator,
            Username = $"{name}-{id:N}",
            DisplayName = name,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    private static void AddHumanMembership(SpringDbContext db, Guid unitId, Guid humanId) =>
        db.UnitMembershipsHumans.Add(new UnitMembershipHumanEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            UnitId = unitId,
            HumanId = humanId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

    private static void AddAgentMembership(SpringDbContext db, Guid unitId, Guid agentId) =>
        db.UnitMemberships.Add(new UnitMembershipEntity
        {
            TenantId = TenantId,
            UnitId = unitId,
            AgentId = agentId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
}
