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
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

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
            ctx.UnitDefinitions.Add(NewUnit("engineering", TenantA));
            ctx.AgentDefinitions.Add(NewAgent("ada", TenantA));
        });
        SeedAsTenant(TenantB, ctx =>
        {
            ctx.UnitDefinitions.Add(NewUnit("marketing", TenantB));
            ctx.AgentDefinitions.Add(NewAgent("hopper", TenantB));
        });
    }

    [Fact]
    public async Task EnsureSameTenantAsync_SameTenantUnitAndAgent_Succeeds()
    {
        var guard = CreateGuard(TenantA);

        await guard.EnsureSameTenantAsync(
            new Address("unit", "engineering"),
            new Address("agent", "ada"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureSameTenantAsync_CrossTenantAgent_ThrowsAndReportsAddresses()
    {
        var guard = CreateGuard(TenantA);

        var ex = await Should.ThrowAsync<CrossTenantMembershipException>(() =>
            guard.EnsureSameTenantAsync(
                new Address("unit", "engineering"),
                new Address("agent", "hopper"),
                TestContext.Current.CancellationToken));

        ex.ParentUnit.Path.ShouldBe("engineering");
        ex.CandidateMember.Path.ShouldBe("hopper");
    }

    [Fact]
    public async Task EnsureSameTenantAsync_CrossTenantUnit_Throws()
    {
        var guard = CreateGuard(TenantA);

        await Should.ThrowAsync<CrossTenantMembershipException>(() =>
            guard.EnsureSameTenantAsync(
                new Address("unit", "engineering"),
                new Address("unit", "marketing"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureSameTenantAsync_UnknownAgent_Throws()
    {
        var guard = CreateGuard(TenantA);

        await Should.ThrowAsync<CrossTenantMembershipException>(() =>
            guard.EnsureSameTenantAsync(
                new Address("unit", "engineering"),
                new Address("agent", "ghost"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShareTenantAsync_SameTenant_ReturnsTrue()
    {
        var guard = CreateGuard(TenantA);

        var result = await guard.ShareTenantAsync(
            new Address("unit", "engineering"),
            new Address("agent", "ada"),
            TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task ShareTenantAsync_CrossTenant_ReturnsFalse()
    {
        var guard = CreateGuard(TenantA);

        var result = await guard.ShareTenantAsync(
            new Address("unit", "engineering"),
            new Address("agent", "hopper"),
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
            new Address("agent", "ada"),
            new Address("agent", "ada"),
            TestContext.Current.CancellationToken);

        result.ShouldBeFalse();
    }

    private UnitMembershipTenantGuard CreateGuard(string currentTenant)
    {
        _context?.Dispose();
        _context = new SpringDbContext(_options, new StaticTenantContext(currentTenant));
        return new UnitMembershipTenantGuard(_context);
    }

    private void SeedAsTenant(string tenantId, Action<SpringDbContext> seed)
    {
        using var ctx = new SpringDbContext(_options, new StaticTenantContext(tenantId));
        seed(ctx);
        ctx.SaveChanges();
    }

    private static UnitDefinitionEntity NewUnit(string id, string tenantId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        UnitId = id,
        ActorId = id,
        Name = id,
        Description = string.Empty,
    };

    private static AgentDefinitionEntity NewAgent(string id, string tenantId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        AgentId = id,
        ActorId = id,
        Name = id,
    };

    public void Dispose()
    {
        _context?.Dispose();
        GC.SuppressFinalize(this);
    }
}