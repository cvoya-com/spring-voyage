// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the "every unit has a parent" invariant introduced
/// by the review feedback on #744:
///
/// - <c>POST /api/v1/units</c> must carry either <c>parentUnitIds</c> or
///   <c>isTopLevel=true</c>. Neither or both is a 400.
/// - Unknown parent-unit id is a 404.
/// - <c>DELETE /api/v1/units/{id}/members/{memberId}</c> refuses the last
///   parent-unit edge from a non-top-level unit with a 409.
/// </summary>
public class UnitParentEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitParentEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateUnit_NeitherParentNorTopLevel_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        ResetMocks();

        var request = new CreateUnitRequest(
            Name: "orphan",
            DisplayName: "Orphan",
            Description: "Unit with no parent",
            Model: null,
            Color: null,
            Connector: null,
            IsTopLevel: null,
            ParentUnitIds: null);

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/units", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        // Directory must never see an orphaned registration.
        await _factory.DirectoryService.DidNotReceive().RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Scheme == "unit"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateUnit_BothParentAndTopLevel_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        ResetMocks();

        var request = new CreateUnitRequest(
            Name: "conflicted",
            DisplayName: "Conflicted",
            Description: "Caller supplied both parent forms",
            Model: null,
            Color: null,
            Connector: null,
            IsTopLevel: true,
            ParentUnitIds: new[] { "some-parent" });

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/units", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.DirectoryService.DidNotReceive().RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Scheme == "unit"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateUnit_TopLevel_Succeeds_PersistsFlag_NoParentEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        ResetMocks();

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        // Seed the UnitDefinition row that RegisterAsync would normally
        // create, so SetTopLevelFlagAsync has something to update.
        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = Guid.NewGuid(),
                TenantId = "default",
                UnitId = "root-unit",
                ActorId = "actor-root",
                Name = "Root Unit",
                Description = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var request = new CreateUnitRequest(
            Name: "root-unit",
            DisplayName: "Root Unit",
            Description: "Tenant-parented",
            IsTopLevel: true);

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/units", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // IsTopLevel should be flipped to true in the DB.
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await verifyDb.UnitDefinitions
            .FirstAsync(u => u.UnitId == "root-unit", ct);
        row.IsTopLevel.ShouldBeTrue();

        // No parent actor proxy should have been called to add the new
        // unit as a member — top-level means no parent-unit edges.
        await proxy.DidNotReceive().AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "root-unit"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateUnit_WithValidParent_Succeeds_WiresParentEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        ResetMocks();

        // Parent unit is registered and reachable.
        var parentAddress = new Address("unit", "eng-team");
        var parentEntry = new DirectoryEntry(
            parentAddress, "actor-parent", "eng-team", "parent", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "eng-team"),
                Arg.Any<CancellationToken>())
            .Returns(parentEntry);
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "child-unit"),
                Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var parentProxy = Substitute.For<IUnitActor>();
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == "actor-parent"),
                Arg.Any<string>())
            .Returns(parentProxy);
        var childProxy = Substitute.For<IUnitActor>();
        childProxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() != "actor-parent"),
                Arg.Any<string>())
            .Returns(childProxy);

        var request = new CreateUnitRequest(
            Name: "child-unit",
            DisplayName: "Child Unit",
            Description: "Parented by eng-team",
            ParentUnitIds: new[] { "eng-team" });

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/units", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await parentProxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "child-unit"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateUnit_UnknownParent_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ResetMocks();

        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var request = new CreateUnitRequest(
            Name: "lost-child",
            DisplayName: "Lost",
            Description: "Parent does not exist",
            ParentUnitIds: new[] { "ghost-unit" });

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/units", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await _factory.DirectoryService.DidNotReceive().RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Scheme == "unit" && e.Address.Path == "lost-child"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMember_NonTopLevelLastParent_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        ResetMocks();

        // Parent unit exists and resolves.
        var parentAddress = new Address("unit", "parent-a");
        var parentEntry = new DirectoryEntry(
            parentAddress, "actor-parent-a", "parent-a", "parent", null, DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "parent-a"),
                Arg.Any<CancellationToken>())
            .Returns(parentEntry);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(Substitute.For<IUnitActor>());

        // Configure the guard to throw, simulating the "last parent of a
        // non-top-level unit" situation.
        _factory.ParentInvariantGuard
            .EnsureParentRemainsAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "parent-a"),
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "child-unit"),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new UnitParentRequiredException(
                "child-unit",
                "parent-a",
                "Cannot remove unit 'child-unit' from unit 'parent-a': this is the unit's last parent. "
                + "Attach it to another parent unit first, promote it to top-level, or delete the unit itself."));

        var response = await _client.DeleteAsync(
            "/api/v1/tenant/units/parent-a/members/child-unit", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private void ResetMocks()
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.ParentInvariantGuard.ClearReceivedCalls();
        // Re-arm the default allow-all stub so tests that don't
        // reconfigure it see the permissive behaviour.
        _factory.ParentInvariantGuard
            .EnsureParentRemainsAsync(
                Arg.Any<Address>(), Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }
}