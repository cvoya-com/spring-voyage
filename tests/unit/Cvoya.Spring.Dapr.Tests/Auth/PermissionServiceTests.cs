// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Tests.TestHelpers;
using Cvoya.Spring.Dapr.Units;

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
/// </summary>
public class PermissionServiceTests
{
    private static readonly Guid HumanGuid = new("aaaaaaaa-bbbb-cccc-dddd-000000000001");
    private static readonly Address HumanCaller = Address.ForIdentity(Address.HumanScheme, HumanGuid);

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

        _service = new PermissionService(
            _permissionStore, _hierarchyResolver, _liveConfigStore, _loggerFactory);
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
            _permissionStore, _hierarchyResolver, failingStore, _loggerFactory);

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
