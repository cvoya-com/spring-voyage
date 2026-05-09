// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PermissionService"/>. Covers the legacy
/// direct-grant API (<see cref="IPermissionService.ResolvePermissionAsync"/>)
/// plus the hierarchy-aware resolver introduced in #414
/// (<see cref="IPermissionService.ResolveEffectivePermissionAsync"/>).
///
/// Post-#2044 the service queries the EF-backed
/// <see cref="IUnitHumanPermissionStore"/> for grants instead of the unit
/// actor's state map; the unit-actor proxy is consulted only for the
/// inheritance flag (which moves to <c>unit_live_config</c> in #2049).
/// The tests therefore stub the store directly and only stub the actor
/// proxy where inheritance reads matter.
/// </summary>
public class PermissionServiceTests
{
    private const string HumanIdString = "aaaaaaaa-bbbb-cccc-dddd-000000000001";
    private static readonly Guid HumanGuid = new(HumanIdString);

    // Stable Guids for each logical unit role used across the suite. Tests
    // pass the no-dash hex form to the service (matches the route shape).
    private static readonly Guid UnitChildId = new("c0c0c0c0-0000-0000-0000-000000000001");
    private static readonly Guid UnitParentId = new("c0c0c0c0-0000-0000-0000-000000000002");
    private static readonly Guid UnitGrandchildId = new("c0c0c0c0-0000-0000-0000-000000000003");
    private static readonly Guid UnitRootId = new("c0c0c0c0-0000-0000-0000-000000000004");
    private static readonly Guid UnitOneId = new("c0c0c0c0-0000-0000-0000-000000000010");

    private readonly IUnitHumanPermissionStore _permissionStore = Substitute.For<IUnitHumanPermissionStore>();
    private readonly IUnitHierarchyResolver _hierarchyResolver = Substitute.For<IUnitHierarchyResolver>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly Dictionary<Guid, IUnitActor> _inheritanceActors = new();
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

        // The actor proxy now serves the inheritance flag only — the grant
        // is read from the EF-backed store. The substitute returns an
        // IUnitActor whose GetPermissionInheritanceAsync defaults to
        // Inherit unless a test overrides it.
        _actorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), nameof(UnitActor))
            .Returns(ci =>
            {
                var idStr = ci.ArgAt<ActorId>(0).GetId();
                return Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(idStr, out var guid)
                    ? InheritanceActor(guid)
                    : InheritanceActor(Guid.Empty);
            });

        _service = new PermissionService(
            _permissionStore, _hierarchyResolver, _actorProxyFactory, _loggerFactory);
    }

    private IUnitActor InheritanceActor(Guid id)
    {
        if (!_inheritanceActors.TryGetValue(id, out var actor))
        {
            actor = Substitute.For<IUnitActor>();
            actor.GetPermissionInheritanceAsync(Arg.Any<CancellationToken>())
                .Returns(UnitPermissionInheritance.Inherit);
            _inheritanceActors[id] = actor;
        }
        return actor;
    }

    private void GrantPermission(Guid unitId, Guid humanId, PermissionLevel level)
    {
        _permissionStore.GetPermissionAsync(unitId, humanId, Arg.Any<CancellationToken>())
            .Returns(level);
    }

    private static string Id(Guid g) => g.ToString("N");

    [Fact]
    public async Task ResolvePermissionAsync_UnitHasPermission_ReturnsPermissionLevel()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitOneId, HumanGuid, PermissionLevel.Operator);

        var result = await _service.ResolvePermissionAsync(HumanIdString, Id(UnitOneId), ct);

        result.ShouldBe(PermissionLevel.Operator);
    }

    [Fact]
    public async Task ResolvePermissionAsync_UnitHasNoPermission_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _service.ResolvePermissionAsync(HumanIdString, Id(UnitOneId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolvePermissionAsync_StoreThrowsException_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        _permissionStore.GetPermissionAsync(UnitOneId, HumanGuid, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Database unavailable"));

        var result = await _service.ResolvePermissionAsync(HumanIdString, Id(UnitOneId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_DirectGrant_ReturnsDirect()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitChildId, HumanGuid, PermissionLevel.Viewer);

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBe(PermissionLevel.Viewer);
        await _hierarchyResolver.DidNotReceive().GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_ParentGrantsOperator_ChildInheritsOperator()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitParentId, HumanGuid, PermissionLevel.Operator);

        _hierarchyResolver.GetParentsAsync(new Address("unit", UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", UnitParentId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBe(PermissionLevel.Operator);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_ExplicitChildDowngrade_OverridesAncestorGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitChildId, HumanGuid, PermissionLevel.Viewer);
        GrantPermission(UnitParentId, HumanGuid, PermissionLevel.Owner);

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBe(PermissionLevel.Viewer);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_ChildOnlyGrant_DoesNotPromoteOnParent()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitChildId, HumanGuid, PermissionLevel.Owner);

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitParentId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_IsolatedChild_DoesNotInheritFromAncestor()
    {
        var ct = TestContext.Current.CancellationToken;
        InheritanceActor(UnitChildId).GetPermissionInheritanceAsync(Arg.Any<CancellationToken>())
            .Returns(UnitPermissionInheritance.Isolated);
        GrantPermission(UnitParentId, HumanGuid, PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address("unit", UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", UnitParentId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_NoParent_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_GrandparentGrants_GrandchildInherits()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitRootId, HumanGuid, PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address("unit", UnitGrandchildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", UnitChildId) });
        _hierarchyResolver.GetParentsAsync(new Address("unit", UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", UnitRootId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitGrandchildId), ct);

        result.ShouldBe(PermissionLevel.Owner);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_IntermediateIsolated_BlocksGrandparent()
    {
        var ct = TestContext.Current.CancellationToken;
        InheritanceActor(UnitChildId).GetPermissionInheritanceAsync(Arg.Any<CancellationToken>())
            .Returns(UnitPermissionInheritance.Isolated);
        GrantPermission(UnitRootId, HumanGuid, PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address("unit", UnitGrandchildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", UnitChildId) });
        _hierarchyResolver.GetParentsAsync(new Address("unit", UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", UnitRootId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitGrandchildId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_NearestGrantWins()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantPermission(UnitChildId, HumanGuid, PermissionLevel.Viewer);
        GrantPermission(UnitRootId, HumanGuid, PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address("unit", UnitGrandchildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", UnitChildId) });
        _hierarchyResolver.GetParentsAsync(new Address("unit", UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", UnitRootId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitGrandchildId), ct);

        result.ShouldBe(PermissionLevel.Viewer);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_InheritanceReadFailure_BlocksAncestorWalk()
    {
        var ct = TestContext.Current.CancellationToken;
        InheritanceActor(UnitChildId).GetPermissionInheritanceAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("state store down"));
        GrantPermission(UnitParentId, HumanGuid, PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address("unit", UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", UnitParentId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_NullOrEmptyIds_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        (await _service.ResolveEffectivePermissionAsync("", Id(UnitOneId), ct)).ShouldBeNull();
        // "h" is not a valid UUID string — falls back to Guid.Empty → null.
        (await _service.ResolveEffectivePermissionAsync("h", "", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_DirectReadThrows_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        _permissionStore.GetPermissionAsync(UnitChildId, HumanGuid, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_NonGuidUnitId_ReturnsNull()
    {
        // A stale / unknown unit id that does not parse as a Guid must
        // surface as "no permission" rather than throwing — match the
        // pre-#2044 directory-miss behaviour.
        var ct = TestContext.Current.CancellationToken;

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, "not-a-guid", ct);

        result.ShouldBeNull();
    }

    // -- #1695 ----------------------------------------------------------
    // Identity-form callers (human:id:<guid>) hand the GUID-hex through
    // PermissionService directly. The pre-fix code blindly passed every
    // humanId string to IHumanIdentityResolver.ResolveByUsernameAsync,
    // which on miss upserted a phantom row keyed by the GUID-hex with
    // its own brand-new UUID — the unit's permission map then 403'd the
    // legitimate caller and the spring.humans table grew a leaking row
    // per send. The guard short-circuits the resolver when humanId
    // already parses as a Guid, so neither symptom can recur.

    [Fact]
    public async Task ResolveHumanGuidAsync_IdentityFormGuid_ResolvesDirectly()
    {
        // Identity-form: 32-hex-char "N" form (the wire shape produced by
        // GuidFormatter.Format and emitted on the From address path
        // component). Must round-trip through the service to the unit
        // permission map without ever calling the resolver.
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<IHumanIdentityResolver>();
        var service = BuildServiceWithResolver(resolver);

        var identityFormHex = HumanGuid.ToString("N");
        GrantPermission(UnitOneId, HumanGuid, PermissionLevel.Owner);

        var result = await service.ResolvePermissionAsync(identityFormHex, Id(UnitOneId), ct);

        result.ShouldBe(PermissionLevel.Owner);
        await resolver.DidNotReceive().ResolveByUsernameAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveHumanGuidAsync_IdentityFormGuid_DashedForm_ResolvesDirectly()
    {
        // GuidFormatter.TryParse accepts both "N" and "D" forms — assert
        // the dashed shape also short-circuits (callers in either form
        // must land on the same row, per the issue's prescription).
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<IHumanIdentityResolver>();
        var service = BuildServiceWithResolver(resolver);

        GrantPermission(UnitOneId, HumanGuid, PermissionLevel.Owner);

        var result = await service.ResolvePermissionAsync(HumanIdString, Id(UnitOneId), ct);

        result.ShouldBe(PermissionLevel.Owner);
        await resolver.DidNotReceive().ResolveByUsernameAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveHumanGuidAsync_LegacyUsername_FallsThroughToResolver()
    {
        // Username-form callers (e.g. "local-dev-user", cloud OAuth
        // usernames) must still flow through the resolver so the
        // upsert-on-first-contact behaviour is preserved.
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<IHumanIdentityResolver>();
        resolver.ResolveByUsernameAsync("local-dev-user", null, Arg.Any<CancellationToken>())
            .Returns(HumanGuid);
        var service = BuildServiceWithResolver(resolver);

        GrantPermission(UnitOneId, HumanGuid, PermissionLevel.Owner);

        var result = await service.ResolvePermissionAsync("local-dev-user", Id(UnitOneId), ct);

        result.ShouldBe(PermissionLevel.Owner);
        await resolver.Received(1).ResolveByUsernameAsync(
            "local-dev-user", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveHumanGuidAsync_IdentityForm_DoesNotCreatePhantomRow()
    {
        // Regression test for the phantom-humans-row symptom in #1695.
        // Sending a message with an identity-form From address used to
        // call ResolveByUsernameAsync(<guid-hex>) → resolver upsert
        // created a row whose username was the GUID-hex. We assert
        // ResolveByUsernameAsync is never invoked for any of the input
        // shapes the AuthenticatedCallerAccessor produces (#1485 / #1491
        // shape: bare "N" form, plus the dashed "D" form for resilience).
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<IHumanIdentityResolver>();
        var service = BuildServiceWithResolver(resolver);

        await service.ResolvePermissionAsync(HumanGuid.ToString("N"), Id(UnitOneId), ct);
        await service.ResolvePermissionAsync(HumanGuid.ToString("D"), Id(UnitOneId), ct);

        await resolver.DidNotReceive().ResolveByUsernameAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private PermissionService BuildServiceWithResolver(IHumanIdentityResolver resolver)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => resolver);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new PermissionService(
            _permissionStore,
            _hierarchyResolver,
            _actorProxyFactory,
            _loggerFactory,
            scopeFactory);
    }
}
