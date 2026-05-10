// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DirectoryUnitHierarchyResolver"/>. Per #2052 /
/// ADR-0040 the resolver reads parent edges directly from
/// <c>unit_subunit_memberships</c>; the explicit tenant-root edge is
/// terminal and must not surface as a unit-shaped parent.
/// </summary>
public class DirectoryUnitHierarchyResolverTests : IDisposable
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid AdaId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ParentId = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid ChildId = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid GrandParentId = new("bbbbbbbb-0000-0000-0000-000000000003");

    private readonly DbContextOptions<SpringDbContext> _options;
    private readonly ILoggerFactory _loggerFactory =
        Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
    private readonly DirectoryUnitHierarchyResolver _resolver;
    private readonly IServiceScope _rootScope;

    public DirectoryUnitHierarchyResolverTests()
    {
        _options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(Tenant));
        services.AddScoped(_ => new SpringDbContext(_options, new StaticTenantContext(Tenant)));
        services.AddScoped<IUnitSubunitMembershipRepository, UnitSubunitMembershipRepository>();
        var provider = services.BuildServiceProvider();
        _rootScope = provider.CreateScope();

        _resolver = new DirectoryUnitHierarchyResolver(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _loggerFactory);
    }

    private void SeedEdge(Guid parent, Guid child)
    {
        using var ctx = new SpringDbContext(_options, new StaticTenantContext(Tenant));
        ctx.UnitSubunitMemberships.Add(new UnitSubunitMembershipEntity
        {
            TenantId = Tenant,
            ParentId = parent,
            ChildId = child,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        ctx.SaveChanges();
    }

    [Fact]
    public async Task GetParentsAsync_AgentAddress_ReturnsEmpty()
    {
        var parents = await _resolver.GetParentsAsync(
            new Address("agent", AdaId), TestContext.Current.CancellationToken);

        parents.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetParentsAsync_SingleUnitParent_ReturnsParent()
    {
        SeedEdge(ParentId, ChildId);

        var parents = await _resolver.GetParentsAsync(
            new Address("unit", ChildId), TestContext.Current.CancellationToken);

        parents.Count.ShouldBe(1);
        parents[0].ShouldBe(new Address("unit", ParentId));
    }

    [Fact]
    public async Task GetParentsAsync_NoContainer_ReturnsEmpty()
    {
        var parents = await _resolver.GetParentsAsync(
            new Address("unit", ChildId), TestContext.Current.CancellationToken);

        parents.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetParentsAsync_TenantRootEdge_FilteredOut()
    {
        // #2052: the explicit tenant-root edge marks the unit as
        // top-level. The hierarchy resolver walks unit → unit links;
        // a tenant-shaped parent must not surface as an inheritance
        // ancestor — the permission walk terminates at the tenant
        // fall-through, not at a unit-typed root.
        SeedEdge(Tenant, ChildId);

        var parents = await _resolver.GetParentsAsync(
            new Address("unit", ChildId), TestContext.Current.CancellationToken);

        parents.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetParentsAsync_MultipleEdges_FiltersTenantRootAndReturnsUnitParents()
    {
        // Defensive: a unit might transiently carry both a tenant-root
        // edge and a unit parent (e.g. mid re-parent). The resolver
        // surfaces only the unit-shaped parents.
        SeedEdge(Tenant, ChildId);
        SeedEdge(ParentId, ChildId);
        SeedEdge(GrandParentId, ChildId);

        var parents = await _resolver.GetParentsAsync(
            new Address("unit", ChildId), TestContext.Current.CancellationToken);

        parents.Count.ShouldBe(2);
        parents.ShouldContain(new Address("unit", ParentId));
        parents.ShouldContain(new Address("unit", GrandParentId));
        parents.ShouldNotContain(new Address("unit", Tenant));
    }

    public void Dispose()
    {
        _rootScope.Dispose();
        GC.SuppressFinalize(this);
    }
}
