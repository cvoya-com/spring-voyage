// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
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
/// Tests for <see cref="DirectoryUnitHierarchyResolver"/>. Covers the
/// "scan directory for parents" path used by the hierarchy-aware
/// permission resolver (#414) when no materialized parent index is
/// available.
/// </summary>
public class DirectoryUnitHierarchyResolverTests
{
    private readonly IDirectoryService _directory = Substitute.For<IDirectoryService>();
    private readonly IActorProxyFactory _proxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly DirectoryUnitHierarchyResolver _resolver;

    private readonly Dictionary<string, Address[]> _memberships = new();

    public DirectoryUnitHierarchyResolverTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _proxyFactory.CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), nameof(UnitActor))
            .Returns(ci =>
            {
                var id = ci.ArgAt<ActorId>(0).GetId();
                var actor = Substitute.For<IUnitActor>();
                var members = _memberships.TryGetValue(id, out var m) ? m : Array.Empty<Address>();
                actor.GetMembersAsync(Arg.Any<CancellationToken>()).Returns(members);
                return actor;
            });

        _resolver = new DirectoryUnitHierarchyResolver(_directory, _proxyFactory, _loggerFactory);
    }

    private DirectoryEntry UnitEntry(string id) =>
        new(new Address("unit", id), id, id, string.Empty, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task GetParentsAsync_AgentAddress_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;

        var parents = await _resolver.GetParentsAsync(new Address("agent", "ada"), ct);

        parents.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetParentsAsync_SingleParentInDirectory_ReturnsParent()
    {
        var ct = TestContext.Current.CancellationToken;
        _memberships["parent"] = new[] { new Address("unit", "child") };

        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { UnitEntry("parent"), UnitEntry("child") });

        var parents = await _resolver.GetParentsAsync(new Address("unit", "child"), ct);

        parents.Count.ShouldBe(1);
        parents[0].ShouldBe(new Address("unit", "parent"));
    }

    [Fact]
    public async Task GetParentsAsync_NoContainer_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { UnitEntry("child") });

        var parents = await _resolver.GetParentsAsync(new Address("unit", "child"), ct);

        parents.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetParentsAsync_DirectoryFails_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("directory down"));

        var parents = await _resolver.GetParentsAsync(new Address("unit", "child"), ct);

        // Fail-safe: return empty so the permission walk degrades to "no
        // inheritance" rather than crashing the caller.
        parents.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetParentsAsync_MemberReadFails_SkipsThatUnit()
    {
        var ct = TestContext.Current.CancellationToken;

        // "flaky" cannot be read; "parent" contains the child and must
        // still be returned.
        _memberships["parent"] = new[] { new Address("unit", "child") };
        _proxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(id => id.GetId() == "flaky"),
                nameof(UnitActor))
            .Returns(ci =>
            {
                var actor = Substitute.For<IUnitActor>();
                actor.GetMembersAsync(Arg.Any<CancellationToken>())
                    .ThrowsAsync(new InvalidOperationException("actor unavailable"));
                return actor;
            });

        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { UnitEntry("parent"), UnitEntry("flaky"), UnitEntry("child") });

        var parents = await _resolver.GetParentsAsync(new Address("unit", "child"), ct);

        parents.Count.ShouldBe(1);
        parents[0].ShouldBe(new Address("unit", "parent"));
    }
}