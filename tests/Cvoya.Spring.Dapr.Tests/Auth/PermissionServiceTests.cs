// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
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
///
/// Post #1629: every unit/agent identifier is a Guid, so the test fixture
/// declares named Guid constants per logical role and threads them through
/// the directory stub.
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

    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IUnitHierarchyResolver _hierarchyResolver = Substitute.For<IUnitHierarchyResolver>();
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly Dictionary<Guid, IUnitActor> _actors = new();
    private readonly PermissionService _service;

    public PermissionServiceTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _actorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), nameof(UnitActor))
            .Returns(ci =>
            {
                var idStr = ci.ArgAt<ActorId>(0).GetId();
                return Guid.TryParse(idStr, out var g) ? Unit(g) : Unit(Guid.Empty);
            });

        // Default: no parents (every unit is a root) and every unit inherits.
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());

        // The directory resolution step added for #976 maps the route-level
        // unit id to its Dapr actor id. With Guid identity the two are now
        // the same value; the substitute returns address.Id so existing
        // assertions that key actors by the unit id continue to work.
        _directoryService.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var address = ci.ArgAt<Address>(0);
                return Task.FromResult<DirectoryEntry?>(new DirectoryEntry(
                    address,
                    address.Id,
                    address.Path,
                    string.Empty,
                    null,
                    DateTimeOffset.UtcNow));
            });

        _service = new PermissionService(
            _actorProxyFactory, _hierarchyResolver, _directoryService, _loggerFactory);
    }

    private IUnitActor Unit(Guid id)
    {
        if (!_actors.TryGetValue(id, out var actor))
        {
            actor = Substitute.For<IUnitActor>();
            actor.GetHumanPermissionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns((PermissionLevel?)null);
            actor.GetPermissionInheritanceAsync(Arg.Any<CancellationToken>())
                .Returns(UnitPermissionInheritance.Inherit);
            _actors[id] = actor;
        }
        return actor;
    }

    private static string Id(Guid g) => g.ToString("N");

    [Fact]
    public async Task ResolvePermissionAsync_UnitHasPermission_ReturnsPermissionLevel()
    {
        var ct = TestContext.Current.CancellationToken;
        Unit(UnitOneId).GetHumanPermissionAsync(HumanGuid, ct).Returns(PermissionLevel.Operator);

        var result = await _service.ResolvePermissionAsync(HumanIdString, Id(UnitOneId), ct);

        result.ShouldBe(PermissionLevel.Operator);
    }

    [Fact]
    public async Task ResolvePermissionAsync_UnitHasNoPermission_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Unit(UnitOneId);

        var result = await _service.ResolvePermissionAsync(HumanIdString, Id(UnitOneId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolvePermissionAsync_ActorThrowsException_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        Unit(UnitOneId).GetHumanPermissionAsync(HumanGuid, ct)
            .ThrowsAsync(new InvalidOperationException("Actor unavailable"));

        var result = await _service.ResolvePermissionAsync(HumanIdString, Id(UnitOneId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_DirectGrant_ReturnsDirect()
    {
        var ct = TestContext.Current.CancellationToken;
        Unit(UnitChildId).GetHumanPermissionAsync(HumanGuid, ct).Returns(PermissionLevel.Viewer);

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBe(PermissionLevel.Viewer);
        await _hierarchyResolver.DidNotReceive().GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_ParentGrantsOperator_ChildInheritsOperator()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Unit(UnitChildId);
        Unit(UnitParentId).GetHumanPermissionAsync(HumanGuid, ct).Returns(PermissionLevel.Operator);

        _hierarchyResolver.GetParentsAsync(new Address("unit", UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", UnitParentId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBe(PermissionLevel.Operator);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_ExplicitChildDowngrade_OverridesAncestorGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        Unit(UnitChildId).GetHumanPermissionAsync(HumanGuid, ct).Returns(PermissionLevel.Viewer);
        Unit(UnitParentId).GetHumanPermissionAsync(HumanGuid, ct).Returns(PermissionLevel.Owner);

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBe(PermissionLevel.Viewer);
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_ChildOnlyGrant_DoesNotPromoteOnParent()
    {
        var ct = TestContext.Current.CancellationToken;
        Unit(UnitChildId).GetHumanPermissionAsync(HumanGuid, ct).Returns(PermissionLevel.Owner);

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitParentId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_IsolatedChild_DoesNotInheritFromAncestor()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Unit(UnitChildId);
        Unit(UnitChildId).GetPermissionInheritanceAsync(Arg.Any<CancellationToken>())
            .Returns(UnitPermissionInheritance.Isolated);
        Unit(UnitParentId).GetHumanPermissionAsync(HumanGuid, ct).Returns(PermissionLevel.Owner);

        _hierarchyResolver.GetParentsAsync(new Address("unit", UnitChildId), Arg.Any<CancellationToken>())
            .Returns(new Address[] { new("unit", UnitParentId) });

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_NoParent_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Unit(UnitChildId);

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_GrandparentGrants_GrandchildInherits()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Unit(UnitGrandchildId);
        _ = Unit(UnitChildId);
        Unit(UnitRootId).GetHumanPermissionAsync(HumanGuid, ct).Returns(PermissionLevel.Owner);

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
        _ = Unit(UnitGrandchildId);
        Unit(UnitChildId).GetPermissionInheritanceAsync(Arg.Any<CancellationToken>())
            .Returns(UnitPermissionInheritance.Isolated);
        Unit(UnitRootId).GetHumanPermissionAsync(HumanGuid, ct).Returns(PermissionLevel.Owner);

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
        _ = Unit(UnitGrandchildId);
        Unit(UnitChildId).GetHumanPermissionAsync(HumanGuid, ct).Returns(PermissionLevel.Viewer);
        Unit(UnitRootId).GetHumanPermissionAsync(HumanGuid, ct).Returns(PermissionLevel.Owner);

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
        _ = Unit(UnitChildId);
        Unit(UnitChildId).GetPermissionInheritanceAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("state store down"));
        Unit(UnitParentId).GetHumanPermissionAsync(HumanGuid, ct).Returns(PermissionLevel.Owner);

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
        Unit(UnitChildId).GetHumanPermissionAsync(HumanGuid, ct)
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _service.ResolveEffectivePermissionAsync(HumanIdString, Id(UnitChildId), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveEffectivePermissionAsync_DirectoryHasNoEntry_ReturnsNull()
    {
        // A stale / unknown unit id must surface as "no permission" rather
        // than silently spinning up an empty actor that reports null either
        // way.
        var ct = TestContext.Current.CancellationToken;

        var directory = Substitute.For<IDirectoryService>();
        directory.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DirectoryEntry?>(null));

        var service = new PermissionService(
            _actorProxyFactory, _hierarchyResolver, directory, _loggerFactory);

        var result = await service.ResolveEffectivePermissionAsync(HumanIdString, Id(Guid.NewGuid()), ct);

        result.ShouldBeNull();
    }
}
