// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Dapr.Tests.TestHelpers;
using Cvoya.Spring.Dapr.Units;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PermissionService"/>. Per ADR-0047 §1 and
/// #2768 the service takes the caller's typed <see cref="Address"/> rather
/// than a free-form string, so the per-scheme behaviour can short-circuit
/// (tenant-user → implicit Owner) without minting phantom Human rows.
///
/// Post-#2044 the service queries the EF-backed
/// <see cref="IUnitHumanPermissionStore"/> for human grants. Per ADR-0040 /
/// #2049 the inheritance flag also lives in EF
/// (<c>unit_live_config.permission_inheritance</c>), so the resolver
/// consults <see cref="IUnitLiveConfigStore"/> for the inheritance
/// lookup — no actor proxy is required anywhere in the walk. The tests
/// therefore stub both stores directly through
/// <see cref="InMemoryUnitLiveConfigStore"/>.
///
/// Post-#2858 the resolver also follows <c>humans.tenant_user_id</c>
/// (ADR-0062 § 1) before the grant-table walk so every Hat bound to the
/// OSS operator inherits the implicit-Owner rule. The tests wire a real
/// EF in-memory <see cref="SpringDbContext"/> through an
/// <see cref="IServiceScopeFactory"/> so the FK lookup runs against a
/// seeded humans table.
/// </summary>
public class PermissionServiceTests
{
    private static readonly Guid HumanGuid = new("aaaaaaaa-bbbb-cccc-dddd-000000000001");
    private static readonly Guid HumanOperatorBoundGuid = new("aaaaaaaa-bbbb-cccc-dddd-000000000002");
    private static readonly Address HumanCaller = Address.ForIdentity(Address.HumanScheme, HumanGuid);
    private static readonly Address HumanOperatorBoundCaller = Address.ForIdentity(Address.HumanScheme, HumanOperatorBoundGuid);

    // Stable Guids for each logical unit role used across the suite.
    private static readonly Guid UnitChildId = new("c0c0c0c0-0000-0000-0000-000000000001");
    private static readonly Guid UnitParentId = new("c0c0c0c0-0000-0000-0000-000000000002");
    private static readonly Guid UnitGrandchildId = new("c0c0c0c0-0000-0000-0000-000000000003");
    private static readonly Guid UnitRootId = new("c0c0c0c0-0000-0000-0000-000000000004");
    private static readonly Guid UnitOneId = new("c0c0c0c0-0000-0000-0000-000000000010");

    private readonly IUnitHumanPermissionStore _permissionStore = Substitute.For<IUnitHumanPermissionStore>();
    private readonly IUnitHierarchyResolver _hierarchyResolver = Substitute.For<IUnitHierarchyResolver>();
    private readonly InMemoryUnitLiveConfigStore _liveConfigStore = new();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PermissionService _service;

    public PermissionServiceTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        // Default: no grants anywhere.
        _permissionStore.GetPermissionAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        // Default: no parents (every unit is a root) and every unit inherits.
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());

        // Wire a real EF in-memory DbContext through a scope factory so the
        // #2858 humans.tenant_user_id lookup runs against seeded data.
        // The default seeded set has:
        //   * HumanGuid           — UNBOUND (tenant_user_id = Guid.Empty); legacy / phantom Hat
        //   * HumanOperatorBoundGuid — BOUND to the OSS operator TenantUser
        _scopeFactory = BuildScopeFactoryWithHumans(
            (HumanGuid, Guid.Empty),
            (HumanOperatorBoundGuid, OssTenantUserIds.Operator));

        _service = new PermissionService(
            _permissionStore, _hierarchyResolver, _liveConfigStore, _scopeFactory, _loggerFactory);
    }

    private static IServiceScopeFactory BuildScopeFactoryWithHumans(
        params (Guid HumanId, Guid TenantUserId)[] humans)
    {
        var dbName = $"PermissionService-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opts => opts.UseInMemoryDatabase(dbName));
        services.AddSingleton<ITenantContext>(new StaticTenantContext(OssTenantIds.Default));
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        foreach (var (id, tuid) in humans)
        {
            db.Humans.Add(new HumanEntity
            {
                Id = id,
                TenantId = OssTenantIds.Default,
                TenantUserId = tuid,
                Username = $"user-{id:N}",
                DisplayName = $"User {id:N}",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        db.SaveChanges();
        return scopeFactory;
    }

    private void GrantPermission(Guid unitId, Guid humanId, PermissionLevel level)
    {
        _permissionStore.GetPermissionAsync(unitId, humanId, Arg.Any<CancellationToken>())
            .Returns(level);
    }

    // -- Direct (unit-only) grants --------------------------------------

    [Fact]
    public async Task ResolvePermissionAsync_HumanCaller_DirectGrant_ReturnsPermissionLevel()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitOneId, HumanGuid, PermissionLevel.Operator);

        var result = await _service.ResolvePermissionAsync(HumanCaller, UnitOneId, ct);

        result.ShouldBe(PermissionLevel.Operator);
    }

    [Fact]
    public async Task ResolvePermissionAsync_HumanCaller_NoGrant_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _service.ResolvePermissionAsync(HumanCaller, UnitOneId, ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolvePermissionAsync_HumanCaller_StoreThrows_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        _permissionStore.GetPermissionAsync(UnitOneId, HumanGuid, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Database unavailable"));

        var result = await _service.ResolvePermissionAsync(HumanCaller, UnitOneId, ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolvePermissionAsync_TenantUserCaller_ReturnsImplicitOwner()
    {
        // #2768: the OSS deployment ships with exactly one TenantUser (the
        // operator); per ADR-0047 the implicit rule is "operator owns every
        // unit in the tenant." The direct grant table is never consulted
        // for a tenant-user caller.
        var ct = TestContext.Current.CancellationToken;
        var caller = Address.ForIdentity(Address.TenantUserScheme, OssTenantUserIds.Operator);

        var result = await _service.ResolvePermissionAsync(caller, UnitOneId, ct);

        result.ShouldBe(PermissionLevel.Owner);
        await _permissionStore.DidNotReceive().GetPermissionAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolvePermissionAsync_HumanCaller_BoundToOperator_ReturnsImplicitOwner()
    {
        // #2858: ADR-0062 § 3 rewrites the auth principal (tenant-user://)
        // to the speaking-as Hat (human://) at the API boundary. Without
        // the FK walk through humans.tenant_user_id, the implicit-Owner
        // short-circuit would be skipped — every operator-driven unit
        // message would 403 until an explicit unit_human_permissions row
        // is planted. The Hat seeded as bound to the operator inherits
        // Owner without consulting the grant table.
        var ct = TestContext.Current.CancellationToken;

        var result = await _service.ResolvePermissionAsync(HumanOperatorBoundCaller, UnitOneId, ct);

        result.ShouldBe(PermissionLevel.Owner);
        await _permissionStore.DidNotReceive().GetPermissionAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolvePermissionAsync_UnknownScheme_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var caller = Address.ForIdentity("agent", HumanGuid);

        var result = await _service.ResolvePermissionAsync(caller, UnitOneId, ct);

        result.ShouldBeNull();
        await _permissionStore.DidNotReceive().GetPermissionAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // -- Effective (hierarchy-aware) walks ------------------------------

    [Fact]
    public async Task ResolveEffectivePermissionAsync_HumanCaller_DirectGrant_ReturnsDirect()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitChildId, HumanGuid, PermissionLevel.Viewer);

        var result = await _service.ResolveEffectivePermissionAsync(HumanCaller, UnitChildId, ct);

        result.ShouldBe(PermissionLevel.Viewer);
        await _hierarchyResolver.DidNotReceive().GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_HumanCaller_ParentGrantsOperator_ChildInheritsOperator()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitParentId, HumanGuid, PermissionLevel.Operator);

        _hierarchyResolver.GetParentsAsync(new Address(Address.UnitScheme, UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new(Address.UnitScheme, UnitParentId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanCaller, UnitChildId, ct);

        result.ShouldBe(PermissionLevel.Operator);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_HumanCaller_ExplicitChildDowngrade_OverridesAncestorGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitChildId, HumanGuid, PermissionLevel.Viewer);
        GrantPermission(UnitParentId, HumanGuid, PermissionLevel.Owner);

        var result = await _service.ResolveEffectivePermissionAsync(HumanCaller, UnitChildId, ct);

        result.ShouldBe(PermissionLevel.Viewer);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_HumanCaller_ChildOnlyGrant_DoesNotPromoteOnParent()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitChildId, HumanGuid, PermissionLevel.Owner);

        var result = await _service.ResolveEffectivePermissionAsync(HumanCaller, UnitParentId, ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_HumanCaller_IsolatedChild_DoesNotInheritFromAncestor()
    {
        var ct = TestContext.Current.CancellationToken;
        _liveConfigStore.SeedInheritance(UnitChildId, UnitPermissionInheritance.Isolated);
        GrantPermission(UnitParentId, HumanGuid, PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address(Address.UnitScheme, UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new(Address.UnitScheme, UnitParentId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanCaller, UnitChildId, ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_HumanCaller_NoParent_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _service.ResolveEffectivePermissionAsync(HumanCaller, UnitChildId, ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_HumanCaller_GrandparentGrants_GrandchildInherits()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitRootId, HumanGuid, PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address(Address.UnitScheme, UnitGrandchildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new(Address.UnitScheme, UnitChildId) });
        _hierarchyResolver.GetParentsAsync(new Address(Address.UnitScheme, UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new(Address.UnitScheme, UnitRootId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanCaller, UnitGrandchildId, ct);

        result.ShouldBe(PermissionLevel.Owner);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_HumanCaller_IntermediateIsolated_BlocksGrandparent()
    {
        var ct = TestContext.Current.CancellationToken;
        _liveConfigStore.SeedInheritance(UnitChildId, UnitPermissionInheritance.Isolated);
        GrantPermission(UnitRootId, HumanGuid, PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address(Address.UnitScheme, UnitGrandchildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new(Address.UnitScheme, UnitChildId) });
        _hierarchyResolver.GetParentsAsync(new Address(Address.UnitScheme, UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new(Address.UnitScheme, UnitRootId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanCaller, UnitGrandchildId, ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_HumanCaller_NearestGrantWins()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitChildId, HumanGuid, PermissionLevel.Viewer);
        GrantPermission(UnitRootId, HumanGuid, PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address(Address.UnitScheme, UnitGrandchildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new(Address.UnitScheme, UnitChildId) });
        _hierarchyResolver.GetParentsAsync(new Address(Address.UnitScheme, UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new(Address.UnitScheme, UnitRootId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanCaller, UnitGrandchildId, ct);

        result.ShouldBe(PermissionLevel.Viewer);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_HumanCaller_InheritanceReadFailure_BlocksAncestorWalk()
    {
        // Wire a throwing substitute store so the inheritance lookup
        // for the child fails. The walk must default to Isolated and
        // not promote the caller via the parent's grant.
        var ct = TestContext.Current.CancellationToken;
        var failingStore = Substitute.For<IUnitLiveConfigStore>();
        failingStore
            .GetPermissionInheritanceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("EF down"));

        var failingService = new PermissionService(
            _permissionStore, _hierarchyResolver, failingStore, _scopeFactory, _loggerFactory);

        GrantPermission(UnitParentId, HumanGuid, PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address(Address.UnitScheme, UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new(Address.UnitScheme, UnitParentId) });

        var result = await failingService.ResolveEffectivePermissionAsync(HumanCaller, UnitChildId, ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_EmptyUnitId_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        (await _service.ResolveEffectivePermissionAsync(HumanCaller, Guid.Empty, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_HumanCaller_DirectReadThrows_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        _permissionStore.GetPermissionAsync(UnitChildId, HumanGuid, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _service.ResolveEffectivePermissionAsync(HumanCaller, UnitChildId, ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_TenantUserCaller_ReturnsImplicitOwner_NoStoreReads()
    {
        // #2768: tenant-user callers short-circuit to Owner without
        // touching the grant table or the hierarchy walker. This is the
        // operator implicit-owner rule the OSS deployment depends on.
        var ct = TestContext.Current.CancellationToken;
        var caller = Address.ForIdentity(Address.TenantUserScheme, OssTenantUserIds.Operator);

        var result = await _service.ResolveEffectivePermissionAsync(caller, UnitChildId, ct);

        result.ShouldBe(PermissionLevel.Owner);
        await _permissionStore.DidNotReceive().GetPermissionAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _hierarchyResolver.DidNotReceive().GetParentsAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_HumanCaller_BoundToOperator_ReturnsImplicitOwner()
    {
        // #2858 core acceptance: a Hat bound to the OSS operator TenantUser
        // inherits implicit Owner uniformly across every unit, including
        // nested ones, without an explicit unit_human_permissions row.
        // The grant table and hierarchy walker must never be consulted —
        // the short-circuit happens before either runs.
        var ct = TestContext.Current.CancellationToken;

        var resultRoot = await _service.ResolveEffectivePermissionAsync(
            HumanOperatorBoundCaller, UnitRootId, ct);
        var resultChild = await _service.ResolveEffectivePermissionAsync(
            HumanOperatorBoundCaller, UnitChildId, ct);
        var resultGrandchild = await _service.ResolveEffectivePermissionAsync(
            HumanOperatorBoundCaller, UnitGrandchildId, ct);

        resultRoot.ShouldBe(PermissionLevel.Owner);
        resultChild.ShouldBe(PermissionLevel.Owner);
        resultGrandchild.ShouldBe(PermissionLevel.Owner);

        await _permissionStore.DidNotReceive().GetPermissionAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _hierarchyResolver.DidNotReceive().GetParentsAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_UnboundHuman_NoGrant_ReturnsNull()
    {
        // #2858: a Human row with tenant_user_id = Guid.Empty (the
        // migration default for legacy rows the seed provider hasn't
        // backfilled yet) does not get the implicit-Owner short-circuit;
        // the walk falls through to the standard grant-table lookup,
        // which returns null because no unit_human_permissions row exists.
        var ct = TestContext.Current.CancellationToken;

        var result = await _service.ResolveEffectivePermissionAsync(HumanCaller, UnitChildId, ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_UnknownHuman_NoGrant_ReturnsNull()
    {
        // A human:// caller whose Human row does not exist in the FK
        // table (no row for this id at all) must not get the implicit
        // short-circuit; falls through to the grant-table lookup which
        // returns null.
        var ct = TestContext.Current.CancellationToken;
        var unknown = Address.ForIdentity(Address.HumanScheme, Guid.NewGuid());

        var result = await _service.ResolveEffectivePermissionAsync(unknown, UnitChildId, ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_UnknownScheme_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var caller = Address.ForIdentity("agent", HumanGuid);

        var result = await _service.ResolveEffectivePermissionAsync(caller, UnitOneId, ct);

        result.ShouldBeNull();
        await _permissionStore.DidNotReceive().GetPermissionAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
