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
/// Tests for <see cref="UnitParentInvariantGuard"/> — the last-parent
/// protection introduced by review feedback on #744 and updated by
/// #2052 / ADR-0040 to recognise the explicit tenant-root edge as the
/// top-level signal. The guard reads the child's parent edges directly
/// from <c>unit_subunit_memberships</c> via the repository so the
/// behaviour matches the EF projection consumed by every other reader.
/// </summary>
public class UnitParentInvariantGuardTests : IDisposable
{
    private static readonly Guid Tenant = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid AdaId = new("bbbbbbbb-2222-2222-2222-000000000001");
    private static readonly Guid TeamId = new("bbbbbbbb-2222-2222-2222-000000000002");
    private static readonly Guid PhantomId = new("bbbbbbbb-2222-2222-2222-000000000003");
    private static readonly Guid TopLevelUnitId = new("bbbbbbbb-2222-2222-2222-000000000004");
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
        var guard = CreateGuard();

        await guard.EnsureParentRemainsAsync(
            new Address("unit", TeamId),
            new Address("agent", AdaId),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_UnregisteredChild_IsNoOp()
    {
        var guard = CreateGuard();

        // No UnitDefinition row for the child — guard treats the
        // removal as a no-op, not a 409. Mirrors the idempotent
        // RemoveMember contract.
        await guard.EnsureParentRemainsAsync(
            new Address("unit", TeamId),
            new Address("unit", PhantomId),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_ChildWithTenantRootEdge_IsNoOp()
    {
        // #2052: the explicit tenant-root edge is the top-level marker.
        // A unit deliberately parented by the tenant is exempt from the
        // last-parent invariant.
        var guard = CreateGuard();
        SeedUnit(TopLevelUnitId);
        SeedParentEdges(TopLevelUnitId, Tenant);

        await guard.EnsureParentRemainsAsync(
            new Address("unit", TeamId),
            new Address("unit", TopLevelUnitId),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_NonTopLevelChildWithMultipleParents_Succeeds()
    {
        var guard = CreateGuard();
        SeedUnit(ChildId);
        SeedParentEdges(ChildId, ParentAId, ParentBId);

        // Child currently has two parents; removing one leaves one.
        await guard.EnsureParentRemainsAsync(
            new Address("unit", ParentAId),
            new Address("unit", ChildId),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_NonTopLevelChildWithLastParent_Throws()
    {
        var guard = CreateGuard();
        SeedUnit(ChildId);
        SeedParentEdges(ChildId, ParentAId);

        var ex = await Should.ThrowAsync<UnitParentRequiredException>(() =>
            guard.EnsureParentRemainsAsync(
                new Address("unit", ParentAId),
                new Address("unit", ChildId),
                TestContext.Current.CancellationToken));

        ex.UnitAddress.ShouldBe(ChildId.ToString("N"));
        ex.ParentUnitId.ShouldBe(ParentAId.ToString("N"));
    }

    [Fact]
    public async Task EnsureParentRemainsAsync_ChildWithBothTenantAndUnitParent_TenantEdgeExempts()
    {
        // Defensive: if a unit somehow carries both a tenant-root edge
        // AND a unit parent (transient state during a re-parent), the
        // tenant-root edge wins and the guard treats the unit as
        // top-level. The repair surface is responsible for retiring
        // either edge cleanly.
        var guard = CreateGuard();
        SeedUnit(ChildId);
        SeedParentEdges(ChildId, Tenant, ParentAId);

        await guard.EnsureParentRemainsAsync(
            new Address("unit", ParentAId),
            new Address("unit", ChildId),
            TestContext.Current.CancellationToken);
    }

    private UnitParentInvariantGuard CreateGuard()
    {
        _context?.Dispose();
        _context = new SpringDbContext(_options, new StaticTenantContext(Tenant));
        var repo = new UnitSubunitMembershipRepository(_context);
        var tenantContext = new StaticTenantContext(Tenant);
        return new UnitParentInvariantGuard(_context, repo, tenantContext);
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
        GC.SuppressFinalize(this);
    }
}
