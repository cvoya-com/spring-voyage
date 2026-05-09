// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitMembershipTenantGuard"/> (#745). The guard
/// consults the tenant-scoped DbSet query filters to decide whether two
/// addresses share the current tenant — a row outside the tenant is
/// filtered out, so the guard treats it as invisible and rejects the
/// edge write.
/// </summary>
public class UnitMembershipTenantGuardTests : IDisposable
{
    private static readonly Guid TenantA = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid TenantB = new("aaaaaaaa-1111-1111-1111-000000000002");

    private static readonly Guid EngineeringId = new("bbbbbbbb-2222-2222-2222-000000000001");
    private static readonly Guid AdaId = new("bbbbbbbb-2222-2222-2222-000000000002");
    private static readonly Guid MarketingId = new("bbbbbbbb-2222-2222-2222-000000000003");
    private static readonly Guid HopperId = new("bbbbbbbb-2222-2222-2222-000000000004");
    private static readonly Guid GhostId = new("bbbbbbbb-2222-2222-2222-000000000005");

    private readonly DbContextOptions<SpringDbContext> _options;
    private SpringDbContext? _context;

    public UnitMembershipTenantGuardTests()
    {
        _options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        // Seed rows from both tenants into the same in-memory store. We
        // flip the tenant context between seed and assert so both tenants
        // exist on disk regardless of which tenant we're currently
        // operating as. The query filter is what gates visibility.
        SeedAsTenant(TenantA, ctx =>
        {
            ctx.UnitDefinitions.Add(NewUnit(EngineeringId, "engineering", TenantA));
            ctx.AgentDefinitions.Add(NewAgent(AdaId, "ada", TenantA));
        });
        SeedAsTenant(TenantB, ctx =>
        {
            ctx.UnitDefinitions.Add(NewUnit(MarketingId, "marketing", TenantB));
            ctx.AgentDefinitions.Add(NewAgent(HopperId, "hopper", TenantB));
        });
    }

    [Fact]
    public async Task EnsureSameTenantAsync_SameTenantUnitAndAgent_Succeeds()
    {
        var guard = CreateGuard(TenantA);

        await guard.EnsureSameTenantAsync(
            new Address("unit", EngineeringId),
            new Address("agent", AdaId),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureSameTenantAsync_CrossTenantAgent_ThrowsAndReportsAddresses()
    {
        var guard = CreateGuard(TenantA);

        var ex = await Should.ThrowAsync<CrossTenantMembershipException>(() =>
            guard.EnsureSameTenantAsync(
                new Address("unit", EngineeringId),
                new Address("agent", HopperId),
                TestContext.Current.CancellationToken));

        ex.ParentUnit.Id.ShouldBe(EngineeringId);
        ex.CandidateMember.Id.ShouldBe(HopperId);
    }

    [Fact]
    public async Task EnsureSameTenantAsync_CrossTenantUnit_Throws()
    {
        var guard = CreateGuard(TenantA);

        await Should.ThrowAsync<CrossTenantMembershipException>(() =>
            guard.EnsureSameTenantAsync(
                new Address("unit", EngineeringId),
                new Address("unit", MarketingId),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureSameTenantAsync_UnknownAgent_Throws()
    {
        var guard = CreateGuard(TenantA);

        await Should.ThrowAsync<CrossTenantMembershipException>(() =>
            guard.EnsureSameTenantAsync(
                new Address("unit", EngineeringId),
                new Address("agent", GhostId),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShareTenantAsync_SameTenant_ReturnsTrue()
    {
        var guard = CreateGuard(TenantA);

        var result = await guard.ShareTenantAsync(
            new Address("unit", EngineeringId),
            new Address("agent", AdaId),
            TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task ShareTenantAsync_CrossTenant_ReturnsFalse()
    {
        var guard = CreateGuard(TenantA);

        var result = await guard.ShareTenantAsync(
            new Address("unit", EngineeringId),
            new Address("agent", HopperId),
            TestContext.Current.CancellationToken);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task ShareTenantAsync_AgentParent_ReturnsFalse()
    {
        // Composition edges only attach to units — guard rejects agent-
        // parents without even consulting the DB.
        var guard = CreateGuard(TenantA);

        var result = await guard.ShareTenantAsync(
            new Address("agent", AdaId),
            new Address("agent", AdaId),
            TestContext.Current.CancellationToken);

        result.ShouldBeFalse();
    }

    private UnitMembershipTenantGuard CreateGuard(Guid currentTenant)
    {
        _context?.Dispose();
        _context = new SpringDbContext(_options, new StaticTenantContext(currentTenant));
        return new UnitMembershipTenantGuard(_context);
    }

    private void SeedAsTenant(Guid tenantId, Action<SpringDbContext> seed)
    {
        using var ctx = new SpringDbContext(_options, new StaticTenantContext(tenantId));
        seed(ctx);
        ctx.SaveChanges();
    }

    private static UnitDefinitionEntity NewUnit(Guid id, string displayName, Guid tenantId) => new()
    {
        Id = id,
        TenantId = tenantId,
        DisplayName = displayName,
        Description = string.Empty,
    };

    private static AgentDefinitionEntity NewAgent(Guid id, string displayName, Guid tenantId) => new()
    {
        Id = id,
        TenantId = tenantId,
        DisplayName = displayName,
    };

    public void Dispose()
    {
        _context?.Dispose();
        GC.SuppressFinalize(this);
    }
}
