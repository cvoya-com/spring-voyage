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
/// <c>IsTopLevel</c> flag and <see cref="IUnitHierarchyResolver"/> for
/// the child's current parent set. The two together gate the
/// 409-worthy case.
/// </summary>
public class UnitParentInvariantGuardTests : IDisposable
{
    private const string Tenant = "t";
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
            Address.For("unit", "team"),
            Address.For("agent", "ada"),
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
            Address.For("unit", "team"),
            Address.For("unit", "phantom"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_TopLevelChild_IsNoOp()
    {
        var (guard, resolver) = CreateGuard();
        SeedUnit("root-unit", isTopLevel: true);

        await guard.EnsureParentRemainsAsync(
            Address.For("unit", "team"),
            Address.For("unit", "root-unit"),
            TestContext.Current.CancellationToken);

        // Top-level: no need to consult the hierarchy resolver, since
        // the parent-required check is short-circuited.
        await resolver.DidNotReceive().GetParentsAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_NonTopLevelChildWithMultipleParents_Succeeds()
    {
        var (guard, resolver) = CreateGuard();
        SeedUnit("child", isTopLevel: false);

        // Child currently has two parents; removing one leaves one.
        resolver.GetParentsAsync(
            Arg.Is<Address>(a => a.Path == "child"),
            Arg.Any<CancellationToken>())
            .Returns(new List<Address>
            {
                new("unit", "parent-a"),
                new("unit", "parent-b"),
            });

        await guard.EnsureParentRemainsAsync(
            Address.For("unit", "parent-a"),
            Address.For("unit", "child"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_NonTopLevelChildWithLastParent_Throws()
    {
        var (guard, resolver) = CreateGuard();
        SeedUnit("child", isTopLevel: false);

        resolver.GetParentsAsync(
            Arg.Is<Address>(a => a.Path == "child"),
            Arg.Any<CancellationToken>())
            .Returns(new List<Address>
            {
                new("unit", "parent-a"),
            });

        var ex = await Should.ThrowAsync<UnitParentRequiredException>(() =>
            guard.EnsureParentRemainsAsync(
                Address.For("unit", "parent-a"),
                Address.For("unit", "child"),
                TestContext.Current.CancellationToken));

        ex.UnitAddress.ShouldBe("child");
        ex.ParentUnitId.ShouldBe("parent-a");
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

    private void SeedUnit(string unitId, bool isTopLevel)
    {
        using var ctx = new SpringDbContext(_options, new StaticTenantContext(Tenant));
        ctx.UnitDefinitions.Add(new UnitDefinitionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            DisplayName = unitId,
            Description = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        ctx.SaveChanges();
    }

    public void Dispose()
    {
        _context?.Dispose();
        GC.SuppressFinalize(this);
    }
}