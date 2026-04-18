// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

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

        _service = new PermissionService(_actorProxyFactory, _hierarchyResolver, _loggerFactory);
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
}