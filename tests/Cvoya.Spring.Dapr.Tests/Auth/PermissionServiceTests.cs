// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

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
/// </summary>
public class PermissionServiceTests
{
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IUnitHierarchyResolver _hierarchyResolver = Substitute.For<IUnitHierarchyResolver>();
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly Dictionary<string, IUnitActor> _actors = new();
    private readonly PermissionService _service;

    public PermissionServiceTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _actorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), nameof(UnitActor))
            .Returns(ci =>
            {
                var id = ci.ArgAt<ActorId>(0).GetId();
                return Unit(id);
            });

        // Default: no parents (every unit is a root) and every unit inherits.
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());

        // The directory resolution step added for #976 maps the route-level
        // unit id to its Dapr actor id. In production the two differ (the
        // actor id is a Guid minted at creation time) but the substitute
        // keeps them identical so existing assertions that key actors by
        // the unit name continue to work unchanged — the point under test
        // is the hierarchy + inheritance logic, not the id mapping itself.
        _directoryService.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var address = ci.ArgAt<Address>(0);
                return Task.FromResult<DirectoryEntry?>(new DirectoryEntry(
                    address,
                    address.Path,
                    address.Path,
                    string.Empty,
                    null,
                    DateTimeOffset.UtcNow));
            });

        _service = new PermissionService(
            _actorProxyFactory, _hierarchyResolver, _directoryService, _loggerFactory);
    }

    private IUnitActor Unit(string id)
    {
        if (!_actors.TryGetValue(id, out var actor))
        {
            actor = Substitute.For<IUnitActor>();
            actor.GetHumanPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((PermissionLevel?)null);
            actor.GetPermissionInheritanceAsync(Arg.Any<CancellationToken>())
                .Returns(UnitPermissionInheritance.Inherit);
            _actors[id] = actor;
        }
        return actor;
    }

    [Fact]
    public async Task ResolvePermissionAsync_UnitHasPermission_ReturnsPermissionLevel()
    {
        var ct = TestContext.Current.CancellationToken;
        Unit("unit-1").GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Operator);

        var result = await _service.ResolvePermissionAsync("human-1", "unit-1", ct);

        result.ShouldBe(PermissionLevel.Operator);
    }

    [Fact]
    public async Task ResolvePermissionAsync_UnitHasNoPermission_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Unit("unit-1");

        var result = await _service.ResolvePermissionAsync("human-1", "unit-1", ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolvePermissionAsync_ActorThrowsException_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        Unit("unit-1").GetHumanPermissionAsync("human-1", ct)
            .ThrowsAsync(new InvalidOperationException("Actor unavailable"));

        var result = await _service.ResolvePermissionAsync("human-1", "unit-1", ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_DirectGrant_ReturnsDirect()
    {
        var ct = TestContext.Current.CancellationToken;
        Unit("child").GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Viewer);

        var result = await _service.ResolveEffectivePermissionAsync("human-1", "child", ct);

        result.ShouldBe(PermissionLevel.Viewer);
        // No hierarchy walk needed when a direct grant is present.
        await _hierarchyResolver.DidNotReceive().GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_ParentGrantsOperator_ChildInheritsOperator()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Unit("child");
        Unit("parent").GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Operator);

        _hierarchyResolver.GetParentsAsync(new Address("unit", "child"), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", "parent") });

        var result = await _service.ResolveEffectivePermissionAsync("human-1", "child", ct);

        result.ShouldBe(PermissionLevel.Operator);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_ExplicitChildDowngrade_OverridesAncestorGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        // Child directly grants Viewer; parent grants Owner. Direct wins.
        Unit("child").GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Viewer);
        Unit("parent").GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Owner);

        var result = await _service.ResolveEffectivePermissionAsync("human-1", "child", ct);

        result.ShouldBe(PermissionLevel.Viewer);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_ChildOnlyGrant_DoesNotPromoteOnParent()
    {
        var ct = TestContext.Current.CancellationToken;
        // A grant on the child unit must not cause the permission service
        // to treat the human as having any permission on the parent.
        Unit("child").GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Owner);

        var result = await _service.ResolveEffectivePermissionAsync("human-1", "parent", ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_IsolatedChild_DoesNotInheritFromAncestor()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Unit("child");
        Unit("child").GetPermissionInheritanceAsync(Arg.Any<CancellationToken>())
            .Returns(UnitPermissionInheritance.Isolated);
        Unit("parent").GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address("unit", "child"), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", "parent") });

        var result = await _service.ResolveEffectivePermissionAsync("human-1", "child", ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_NoParent_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Unit("child");

        var result = await _service.ResolveEffectivePermissionAsync("human-1", "child", ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_GrandparentGrants_GrandchildInherits()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Unit("grandchild");
        _ = Unit("child");
        Unit("root").GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address("unit", "grandchild"), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", "child") });
        _hierarchyResolver.GetParentsAsync(new Address("unit", "child"), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", "root") });

        var result = await _service.ResolveEffectivePermissionAsync("human-1", "grandchild", ct);

        result.ShouldBe(PermissionLevel.Owner);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_IntermediateIsolated_BlocksGrandparent()
    {
        var ct = TestContext.Current.CancellationToken;
        // grandchild -> child (isolated) -> root (owner). The isolated
        // intermediate unit blocks the root's authority from flowing down.
        _ = Unit("grandchild");
        Unit("child").GetPermissionInheritanceAsync(Arg.Any<CancellationToken>())
            .Returns(UnitPermissionInheritance.Isolated);
        Unit("root").GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address("unit", "grandchild"), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", "child") });
        _hierarchyResolver.GetParentsAsync(new Address("unit", "child"), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", "root") });

        var result = await _service.ResolveEffectivePermissionAsync("human-1", "grandchild", ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_NearestGrantWins()
    {
        var ct = TestContext.Current.CancellationToken;
        // grandchild -> child (grants Viewer) -> root (grants Owner).
        // The nearest grant wins: Viewer, not Owner.
        _ = Unit("grandchild");
        Unit("child").GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Viewer);
        Unit("root").GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address("unit", "grandchild"), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", "child") });
        _hierarchyResolver.GetParentsAsync(new Address("unit", "child"), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", "root") });

        var result = await _service.ResolveEffectivePermissionAsync("human-1", "grandchild", ct);

        result.ShouldBe(PermissionLevel.Viewer);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_InheritanceReadFailure_BlocksAncestorWalk()
    {
        var ct = TestContext.Current.CancellationToken;
        // If the platform cannot confirm the inheritance flag on the child,
        // it must fail closed and block ancestor authority rather than
        // silently granting. Confused-deputy defence.
        _ = Unit("child");
        Unit("child").GetPermissionInheritanceAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("state store down"));
        Unit("parent").GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address("unit", "child"), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", "parent") });

        var result = await _service.ResolveEffectivePermissionAsync("human-1", "child", ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_NullOrEmptyIds_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        (await _service.ResolveEffectivePermissionAsync("", "u", ct)).ShouldBeNull();
        (await _service.ResolveEffectivePermissionAsync("h", "", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_DirectReadThrows_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        Unit("child").GetHumanPermissionAsync("human-1", ct)
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _service.ResolveEffectivePermissionAsync("human-1", "child", ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_ResolvesRouteIdToActorIdBeforeProxy()
    {
        // #976: the route `{id}` is the unit name, but the actor is keyed
        // under a Guid. The permission service must resolve the directory
        // entry first so it reads the authoritative permission state; if
        // it addresses the proxy by the route id directly it hits a
        // freshly activated actor with an empty permission map and every
        // `/humans/*` call 403s.
        var ct = TestContext.Current.CancellationToken;
        const string RouteId = "my-unit";
        const string ActorId = "11111111-2222-3333-4444-555555555555";

        var actor = Substitute.For<IUnitActor>();
        actor.GetHumanPermissionAsync("human-1", Arg.Any<CancellationToken>())
            .Returns(PermissionLevel.Owner);

        var directory = Substitute.For<IDirectoryService>();
        directory.ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == RouteId),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", RouteId),
                ActorId,
                RouteId,
                string.Empty,
                null,
                DateTimeOffset.UtcNow));

        var proxyFactory = Substitute.For<IActorProxyFactory>();
        proxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == ActorId), nameof(UnitActor))
            .Returns(actor);

        var service = new PermissionService(
            proxyFactory, _hierarchyResolver, directory, _loggerFactory);

        var result = await service.ResolveEffectivePermissionAsync("human-1", RouteId, ct);

        result.ShouldBe(PermissionLevel.Owner);

        // Crucially the service must NOT address the proxy by the raw
        // route id; doing so reads a different (empty) actor instance.
        proxyFactory.DidNotReceive().CreateActorProxy<IUnitActor>(
            Arg.Is<ActorId>(a => a.GetId() == RouteId), nameof(UnitActor));
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_DirectoryHasNoEntry_ReturnsNull()
    {
        // A stale / unknown unit id must surface as "no permission" rather
        // than silently spinning up an empty actor that reports null either
        // way. The behaviour needs to be explicit so callers' 403s remain
        // stable if the directory loses a row.
        var ct = TestContext.Current.CancellationToken;

        var directory = Substitute.For<IDirectoryService>();
        directory.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DirectoryEntry?>(null));

        var service = new PermissionService(
            _actorProxyFactory, _hierarchyResolver, directory, _loggerFactory);

        var result = await service.ResolveEffectivePermissionAsync("human-1", "ghost-unit", ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolvePermissionAsync_ResolvesRouteIdToActorIdBeforeProxy()
    {
        // Mirror of the effective-permission regression — the direct
        // resolver must also consult the directory so callers that use
        // `ResolvePermissionAsync` (e.g. unit-editor surfaces) read the
        // authoritative permission state instead of an empty actor.
        var ct = TestContext.Current.CancellationToken;
        const string RouteId = "unit-direct";
        const string ActorId = "99999999-8888-7777-6666-555555555555";

        var actor = Substitute.For<IUnitActor>();
        actor.GetHumanPermissionAsync("human-1", Arg.Any<CancellationToken>())
            .Returns(PermissionLevel.Operator);

        var directory = Substitute.For<IDirectoryService>();
        directory.ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == RouteId),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", RouteId),
                ActorId,
                RouteId,
                string.Empty,
                null,
                DateTimeOffset.UtcNow));

        var proxyFactory = Substitute.For<IActorProxyFactory>();
        proxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == ActorId), nameof(UnitActor))
            .Returns(actor);

        var service = new PermissionService(
            proxyFactory, _hierarchyResolver, directory, _loggerFactory);

        var result = await service.ResolvePermissionAsync("human-1", RouteId, ct);

        result.ShouldBe(PermissionLevel.Operator);
    }
}