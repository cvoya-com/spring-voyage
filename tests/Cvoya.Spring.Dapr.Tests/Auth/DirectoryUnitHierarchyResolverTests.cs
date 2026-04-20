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

using Microsoft.Extensions.DependencyInjection;
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
    private readonly IUnitMembershipTenantGuard _tenantGuard = Substitute.For<IUnitMembershipTenantGuard>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly DirectoryUnitHierarchyResolver _resolver;

    private readonly Dictionary<string, Address[]> _memberships = new();

    public DirectoryUnitHierarchyResolverTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        // Default the guard to allow everything — per-test overrides below
        // seed the cross-tenant rejection cases explicitly.
        _tenantGuard.ShareTenantAsync(Arg.Any<Address>(), Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _proxyFactory.CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), nameof(UnitActor))
            .Returns(ci =>
            {
                var id = ci.ArgAt<ActorId>(0).GetId();
                var actor = Substitute.For<IUnitActor>();
                var members = _memberships.TryGetValue(id, out var m) ? m : Array.Empty<Address>();
                actor.GetMembersAsync(Arg.Any<CancellationToken>()).Returns(members);
                return actor;
            });

        // The production resolver takes IServiceScopeFactory and resolves
        // the tenant guard per call so the scoped guard can lease a
        // SpringDbContext. For tests we hand-build a provider that holds
        // the guard substitute.
        var services = new ServiceCollection();
        services.AddSingleton(_tenantGuard);
        var provider = services.BuildServiceProvider();

        _resolver = new DirectoryUnitHierarchyResolver(
            _directory, _proxyFactory, provider.GetRequiredService<IServiceScopeFactory>(), _loggerFactory);
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

    [Fact]
    public async Task GetParentsAsync_CrossTenantCandidate_Skipped()
    {
        // #745: even if actor state (hypothetically seeded) points from a
        // different-tenant parent back to the child unit, the resolver
        // must not surface the parent because the permission walk would
        // incorrectly promote the child across tenants.
        var ct = TestContext.Current.CancellationToken;

        // Arrange a candidate "foreign-parent" whose actor state claims
        // the child as a member — but the tenant guard reports them as
        // cross-tenant, so the resolver must skip it.
        _memberships["foreign-parent"] = new[] { new Address("unit", "child") };
        _tenantGuard
            .ShareTenantAsync(
                Arg.Is<Address>(a => a.Path == "foreign-parent"),
                Arg.Is<Address>(a => a.Path == "child"),
                Arg.Any<CancellationToken>())
            .Returns(false);

        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { UnitEntry("foreign-parent"), UnitEntry("child") });

        var parents = await _resolver.GetParentsAsync(new Address("unit", "child"), ct);

        parents.ShouldBeEmpty();
        // Defensive: we must never have read the foreign parent's
        // members — the guard short-circuits before the actor call.
        _proxyFactory.DidNotReceive().CreateActorProxy<IUnitActor>(
            Arg.Is<ActorId>(id => id.GetId() == "foreign-parent"),
            Arg.Any<string>());
    }
}