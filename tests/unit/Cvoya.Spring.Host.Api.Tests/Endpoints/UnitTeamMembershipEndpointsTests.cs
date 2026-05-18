// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// REST-level tests for the team-role membership endpoints (#2409 /
/// ADR-0044 § 3, reshaped by ADR-0046 §7). Covers POST idempotency on the
/// natural key, GET / PATCH / DELETE happy paths, the 404 surface on
/// unknown unit / unknown human / unknown row, and the existence-first
/// ordering inherited from the surrounding <see cref="UnitPermissionCheck"/>
/// helper (#1029). The natural key is now <c>(unit, human)</c>; the row
/// carries a multi-valued <c>roles</c> jsonb list.
/// </summary>
public class UnitTeamMembershipEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitTeamMembershipEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Add_NewRow_Returns200_PersistsMembership()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);
        var humanId = await SeedHumanAsync("alice");

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId,
                new[] { "owner" },
                new[] { "security", "release-mgmt" },
                new[] { "escalation" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitHumanMemberResponse>(ct);
        body.ShouldNotBeNull();
        body!.HumanId.ShouldBe(humanId);
        body.Roles.ShouldBe(new[] { "owner" });
        body.Expertise.ShouldBe(new[] { "security", "release-mgmt" });
        body.Notifications.ShouldBe(new[] { "escalation" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.UnitMembershipsHumans
            .AsNoTracking()
            .SingleAsync(m => m.UnitId == unitId && m.HumanId == humanId, ct);
        row.Roles.ShouldBe(new[] { "owner" });
        row.Expertise.ShouldBe(new[] { "security", "release-mgmt" });
        row.Notifications.ShouldBe(new[] { "escalation" });
    }

    [Fact]
    public async Task Add_SameTuple_IsIdempotent_UpdatesProjections()
    {
        // ADR-0046 §7: the natural key is (tenant, unit, human). A re-POST
        // with the same tuple does not 409 — it overwrites roles +
        // expertise + notifications in place.
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);
        var humanId = await SeedHumanAsync("alice");

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId, new[] { "owner" }, new[] { "old" }, new[] { "old-evt" }),
            ct);
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId, new[] { "reviewer" }, new[] { "new" }, new[] { "new-evt" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitHumanMemberResponse>(ct);
        body!.Roles.ShouldBe(new[] { "reviewer" });
        body.Expertise.ShouldBe(new[] { "new" });
        body.Notifications.ShouldBe(new[] { "new-evt" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = await db.UnitMembershipsHumans
            .AsNoTracking()
            .Where(m => m.UnitId == unitId && m.HumanId == humanId)
            .ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].Roles.ShouldBe(new[] { "reviewer" });
        rows[0].Expertise.ShouldBe(new[] { "new" });
    }

    [Fact]
    public async Task Add_UnknownHuman_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(Guid.NewGuid(), new[] { "owner" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Add_EmptyHumanId_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(Guid.Empty, new[] { "owner" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Add_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeNotFound(unitId);
        _factory.PermissionService
            .ResolveEffectivePermissionAsync(
                AuthConstants.DefaultLocalUserId, unitId.ToString("N"), Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(Guid.NewGuid(), new[] { "owner" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Add_CallerHasOnlyViewer_Returns403()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Viewer);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(Guid.NewGuid(), new[] { "owner" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_ReturnsRowsForViewer()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);

        var alice = await SeedHumanAsync("alice");
        var bob = await SeedHumanAsync("bob");

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(alice, new[] { "owner" }), ct);
        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(bob, new[] { "reviewer" }), ct);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<UnitHumanMemberResponse>>(ct);
        rows.ShouldNotBeNull();
        rows!.Count.ShouldBe(2);
        rows.ShouldContain(r => r.HumanId == alice && r.Roles.SequenceEqual(new[] { "owner" }));
        rows.ShouldContain(r => r.HumanId == bob && r.Roles.SequenceEqual(new[] { "reviewer" }));
    }

    [Fact]
    public async Task List_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeNotFound(unitId);
        _factory.PermissionService
            .ResolveEffectivePermissionAsync(
                AuthConstants.DefaultLocalUserId, unitId.ToString("N"), Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_UpdatesProjections()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);
        var humanId = await SeedHumanAsync("alice");

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId, new[] { "owner" }, new[] { "old" }, new[] { "old-evt" }),
            ct);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans/{humanId:N}",
            new UpdateUnitHumanMemberRequest(
                new[] { "owner", "reviewer" }, new[] { "patched" }, new[] { "patched-evt" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitHumanMemberResponse>(ct);
        body!.Roles.ShouldBe(new[] { "owner", "reviewer" });
        body.Expertise.ShouldBe(new[] { "patched" });
        body.Notifications.ShouldBe(new[] { "patched-evt" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.UnitMembershipsHumans
            .AsNoTracking()
            .SingleAsync(m => m.UnitId == unitId && m.HumanId == humanId, ct);
        row.Roles.ShouldBe(new[] { "owner", "reviewer" });
        row.Expertise.ShouldBe(new[] { "patched" });
        row.Notifications.ShouldBe(new[] { "patched-evt" });
    }

    [Fact]
    public async Task Patch_UnknownRow_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans/{Guid.NewGuid():N}",
            new UpdateUnitHumanMemberRequest(new[] { "owner" }, new[] { "x" }, Array.Empty<string>()),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ExistingRow_Returns204_RemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);
        var humanId = await SeedHumanAsync("alice");

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId, new[] { "owner" }), ct);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans/{humanId:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        (await db.UnitMembershipsHumans.AsNoTracking().AnyAsync(
            m => m.UnitId == unitId && m.HumanId == humanId, ct))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_NoSuchRow_Returns204_Idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans/{Guid.NewGuid():N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_CallerHasOnlyViewer_Returns403()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Viewer);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans/{Guid.NewGuid():N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task InstallSeededRow_RePostedManually_IsIdempotent()
    {
        // ADR-0046 §7: the install path and the REST add path both write
        // to the same EF table. Re-asserting the same (unit, human) tuple
        // through the REST surface overwrites the row's roles / expertise /
        // notifications in place rather than producing a duplicate row.
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);
        var humanId = await SeedHumanAsync("alice");

        Guid installedMembershipId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            installedMembershipId = Guid.NewGuid();
            db.UnitMembershipsHumans.Add(new UnitMembershipHumanEntity
            {
                Id = installedMembershipId,
                TenantId = OssTenantIds.Default,
                UnitId = unitId,
                HumanId = humanId,
                Roles = new List<string> { "owner" },
                Expertise = new List<string> { "from-package" },
                Notifications = new List<string>(),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId,
                new[] { "owner", "reviewer" },
                new[] { "from-cli" },
                new[] { "escalation" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitHumanMemberResponse>(ct);
        body!.MembershipId.ShouldBe(installedMembershipId);
        body.Roles.ShouldBe(new[] { "owner", "reviewer" });
        body.Expertise.ShouldBe(new[] { "from-cli" });
        body.Notifications.ShouldBe(new[] { "escalation" });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var rows = await db.UnitMembershipsHumans
                .AsNoTracking()
                .Where(m => m.UnitId == unitId && m.HumanId == humanId)
                .ToListAsync(ct);
            rows.Count.ShouldBe(1);
            rows[0].Id.ShouldBe(installedMembershipId);
        }
    }

    private void ArrangeResolved(Guid unitId)
    {
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == unitId),
                Arg.Any<CancellationToken>())
            .Returns(_ => new DirectoryEntry(
                new Address("unit", unitId),
                unitId,
                "Test unit",
                "Test unit",
                null,
                DateTimeOffset.UtcNow));
    }

    private void ArrangeNotFound(Guid unitId)
    {
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == unitId),
                Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);
    }

    private void ArrangePermission(Guid unitId, string humanId, PermissionLevel level)
    {
        _factory.PermissionService
            .ResolveEffectivePermissionAsync(humanId, unitId.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(level);
    }

    private async Task<Guid> SeedHumanAsync(string username)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var id = Guid.NewGuid();
        db.Humans.Add(new HumanEntity
        {
            Id = id,
            TenantId = OssTenantIds.Default,
            Username = $"{username}-{id:N}",
            DisplayName = username,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return id;
    }
}
