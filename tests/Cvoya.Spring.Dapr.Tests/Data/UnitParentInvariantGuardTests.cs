// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitParentInvariantGuard"/> — the last-parent
/// protection introduced by the review feedback on #744. The guard
/// consults <see cref="SpringDbContext"/> for the child's
/// "is top-level" derivation and <see cref="IUnitHierarchyResolver"/>
/// for the child's current parent set. The two together gate the
/// 409-worthy case.
/// </summary>
public class UnitParentInvariantGuardTests : IDisposable
{
    private static readonly Guid Tenant = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid AdaId = new("bbbbbbbb-2222-2222-2222-000000000001");
    private static readonly Guid TeamId = new("bbbbbbbb-2222-2222-2222-000000000002");
    private static readonly Guid PhantomId = new("bbbbbbbb-2222-2222-2222-000000000003");
    private static readonly Guid RootUnitId = new("bbbbbbbb-2222-2222-2222-000000000004");
    private static readonly Guid ChildId = new("bbbbbbbb-2222-2222-2222-000000000005");
    private static readonly Guid ParentAId = new("bbbbbbbb-2222-2222-2222-000000000006");
    private static readonly Guid ParentBId = new("bbbbbbbb-2222-2222-2222-000000000007");

    private readonly DbContextOptions<SpringDbContext> _options;
    private SpringDbContext? _context;

    public UnitParentInvariantGuardTests()
    {
        _options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_AgentChild_IsNoOp()
    {
        var (guard, _) = CreateGuard();

        await guard.EnsureParentRemainsAsync(
            new Address("unit", TeamId),
            new Address("agent", AdaId),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_UnregisteredChild_IsNoOp()
    {
        var (guard, _) = CreateGuard();

        // No UnitDefinition row for the child — guard treats the
        // removal as a no-op, not a 409. Mirrors the idempotent
        // RemoveMember contract.
        await guard.EnsureParentRemainsAsync(
            new Address("unit", TeamId),
            new Address("unit", PhantomId),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_TopLevelChild_IsNoOp()
    {
        var (guard, resolver) = CreateGuard();
        SeedUnit(RootUnitId);

        // No incoming parent edges seeded — RootUnitId is implicitly top-level.
        await guard.EnsureParentRemainsAsync(
            new Address("unit", TeamId),
            new Address("unit", RootUnitId),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_NonTopLevelChildWithMultipleParents_Succeeds()
    {
        var (guard, resolver) = CreateGuard();
        SeedUnit(ChildId);
        SeedParentEdges(ChildId, ParentAId, ParentBId);

        // Child currently has two parents; removing one leaves one.
        resolver.GetParentsAsync(
            Arg.Is<Address>(a => a.Id == ChildId),
            Arg.Any<CancellationToken>())
            .Returns(new List<Address>
            {
                new("unit", ParentAId),
                new("unit", ParentBId),
            });

        await guard.EnsureParentRemainsAsync(
            new Address("unit", ParentAId),
            new Address("unit", ChildId),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_NonTopLevelChildWithLastParent_Throws()
    {
        var (guard, resolver) = CreateGuard();
        SeedUnit(ChildId);
        SeedParentEdges(ChildId, ParentAId);

        resolver.GetParentsAsync(
            Arg.Is<Address>(a => a.Id == ChildId),
            Arg.Any<CancellationToken>())
            .Returns(new List<Address>
            {
                new("unit", ParentAId),
            });

        var ex = await Should.ThrowAsync<UnitParentRequiredException>(() =>
            guard.EnsureParentRemainsAsync(
                new Address("unit", ParentAId),
                new Address("unit", ChildId),
                TestContext.Current.CancellationToken));

        ex.UnitAddress.ShouldBe(ChildId.ToString("N"));
        ex.ParentUnitId.ShouldBe(ParentAId.ToString("N"));
    }

    private (UnitParentInvariantGuard Guard, IUnitHierarchyResolver Resolver) CreateGuard()
    {
        _context?.Dispose();
        _context = new SpringDbContext(_options, new StaticTenantContext(Tenant));
        var resolver = Substitute.For<IUnitHierarchyResolver>();
        resolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());
        return (new UnitParentInvariantGuard(_context, resolver), resolver);
    }

    private void SeedUnit(Guid unitId)
    {
        using var ctx = new SpringDbContext(_options, new StaticTenantContext(Tenant));
        ctx.UnitDefinitions.Add(new UnitDefinitionEntity
        {
            Id = unitId,
            TenantId = Tenant,
            DisplayName = unitId.ToString("N"),
            Description = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        ctx.SaveChanges();
    }

    private void SeedParentEdges(Guid childId, params Guid[] parentIds)
    {
        using var ctx = new SpringDbContext(_options, new StaticTenantContext(Tenant));
        foreach (var parentId in parentIds)
        {
            ctx.UnitSubunitMemberships.Add(new UnitSubunitMembershipEntity
            {
                TenantId = Tenant,
                ParentId = parentId,
                ChildId = childId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        ctx.SaveChanges();
    }

    public void Dispose()
    {
        _context?.Dispose();
    }
}
