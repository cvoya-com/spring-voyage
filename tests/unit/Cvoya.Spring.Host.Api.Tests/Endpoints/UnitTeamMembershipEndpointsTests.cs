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
/// ADR-0044 § 3). Covers POST idempotency on the natural key, GET / PATCH
/// / DELETE happy paths, the 404 surface on unknown unit / unknown
/// human / unknown row, and the existence-first ordering inherited from
/// the surrounding <see cref="UnitPermissionCheck"/> helper (#1029).
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
            new AddUnitHumanMemberRequest(humanId, "owner",
                new[] { "security", "release-mgmt" },
                new[] { "escalation" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitHumanMemberResponse>(ct);
        body.ShouldNotBeNull();
        body!.HumanId.ShouldBe(humanId);
        body.Role.ShouldBe("owner");
        body.Expertise.ShouldBe(new[] { "security", "release-mgmt" });
        body.Notifications.ShouldBe(new[] { "escalation" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.UnitMembershipsHumans
            .AsNoTracking()
            .SingleAsync(m => m.UnitId == unitId && m.HumanId == humanId && m.Role == "owner", ct);
        row.Expertise.ShouldBe(new[] { "security", "release-mgmt" });
        row.Notifications.ShouldBe(new[] { "escalation" });
    }

    [Fact]
    public async Task Add_SameTuple_IsIdempotent_UpdatesProjections()
    {
        // ADR-0044 § 3: the natural key is (tenant, unit, human, role). A
        // re-POST with the same tuple does not 409 — it overwrites
        // expertise + notifications in place, matching the auto-seed
        // pattern adopted by the #2408 connector-identity surface.
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);
        var humanId = await SeedHumanAsync("alice");

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId, "owner", new[] { "old" }, new[] { "old-evt" }),
            ct);
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId, "owner", new[] { "new" }, new[] { "new-evt" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitHumanMemberResponse>(ct);
        body!.Expertise.ShouldBe(new[] { "new" });
        body.Notifications.ShouldBe(new[] { "new-evt" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = await db.UnitMembershipsHumans
            .AsNoTracking()
            .Where(m => m.UnitId == unitId && m.HumanId == humanId && m.Role == "owner")
            .ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].Expertise.ShouldBe(new[] { "new" });
    }

    [Fact]
    public async Task Add_SameHumanDifferentRole_CreatesSecondRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);
        var humanId = await SeedHumanAsync("alice");

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId, "owner"), ct);
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId, "reviewer"), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = await db.UnitMembershipsHumans
            .AsNoTracking()
            .Where(m => m.UnitId == unitId && m.HumanId == humanId)
            .OrderBy(m => m.Role)
            .ToListAsync(ct);
        rows.Count.ShouldBe(2);
        rows[0].Role.ShouldBe("owner");
        rows[1].Role.ShouldBe("reviewer");
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
            new AddUnitHumanMemberRequest(Guid.NewGuid(), "owner"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Add_BlankRole_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);
        var humanId = await SeedHumanAsync("alice");

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId, "   "),
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
            new AddUnitHumanMemberRequest(Guid.NewGuid(), "owner"),
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
            new AddUnitHumanMemberRequest(Guid.NewGuid(), "owner"),
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
            new AddUnitHumanMemberRequest(alice, "owner"), ct);
        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(bob, "reviewer"), ct);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<UnitHumanMemberResponse>>(ct);
        rows.ShouldNotBeNull();
        rows!.Count.ShouldBe(2);
        rows.ShouldContain(r => r.HumanId == alice && r.Role == "owner");
        rows.ShouldContain(r => r.HumanId == bob && r.Role == "reviewer");
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
    public async Task Patch_UpdatesProjectionsWithoutTouchingRole()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);
        var humanId = await SeedHumanAsync("alice");

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId, "owner", new[] { "old" }, new[] { "old-evt" }),
            ct);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans/{humanId:N}/owner",
            new UpdateUnitHumanMemberRequest(new[] { "patched" }, new[] { "patched-evt" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitHumanMemberResponse>(ct);
        body!.Role.ShouldBe("owner");
        body.Expertise.ShouldBe(new[] { "patched" });
        body.Notifications.ShouldBe(new[] { "patched-evt" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.UnitMembershipsHumans
            .AsNoTracking()
            .SingleAsync(m => m.UnitId == unitId && m.HumanId == humanId && m.Role == "owner", ct);
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
            $"/api/v1/tenant/units/{unitId:N}/members/humans/{Guid.NewGuid():N}/owner",
            new UpdateUnitHumanMemberRequest(new[] { "x" }, Array.Empty<string>()),
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
            new AddUnitHumanMemberRequest(humanId, "owner"), ct);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans/{humanId:N}/owner", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        (await db.UnitMembershipsHumans.AsNoTracking().AnyAsync(
            m => m.UnitId == unitId && m.HumanId == humanId && m.Role == "owner", ct))
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
            $"/api/v1/tenant/units/{unitId:N}/members/humans/{Guid.NewGuid():N}/owner", ct);

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
            $"/api/v1/tenant/units/{unitId:N}/members/humans/{Guid.NewGuid():N}/owner", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task InstallSeededRow_CoexistsWithManualAdd_OnDifferentRole()
    {
        // Coexistence check from the #2409 issue body: the package install
        // path (DefaultPackageArtefactActivator.UpsertMembershipAsync) and
        // the new manual-add endpoint both write to the same EF table.
        // Adding the same human under a *different* role through the REST
        // surface must leave the install-seeded row intact.
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        ArrangePermission(unitId, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);
        var humanId = await SeedHumanAsync("alice");

        // Simulate the install activator's direct EF write.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitMembershipsHumans.Add(new UnitMembershipHumanEntity
            {
                Id = Guid.NewGuid(),
                TenantId = OssTenantIds.Default,
                UnitId = unitId,
                HumanId = humanId,
                Role = "owner",
                Expertise = new List<string> { "package-declared" },
                Notifications = new List<string>(),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        // Manual-add path adds the same human under a different role.
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId, "reviewer", new[] { "manual" }, Array.Empty<string>()),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var rows = await db.UnitMembershipsHumans
                .AsNoTracking()
                .Where(m => m.UnitId == unitId && m.HumanId == humanId)
                .OrderBy(m => m.Role)
                .ToListAsync(ct);
            rows.Count.ShouldBe(2);
            rows[0].Role.ShouldBe("owner");
            rows[0].Expertise.ShouldBe(new[] { "package-declared" });
            rows[1].Role.ShouldBe("reviewer");
            rows[1].Expertise.ShouldBe(new[] { "manual" });
        }
    }

    [Fact]
    public async Task InstallSeededRow_RePostedManually_IsIdempotent()
    {
        // The "duplicate-add is a no-op" branch the issue body lets us pick:
        // a manual POST with the same (human, role) tuple the install path
        // already wrote updates expertise + notifications instead of
        // returning 409, matching the auto-seed contract from #2420.
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
                Role = "owner",
                Expertise = new List<string> { "from-package" },
                Notifications = new List<string>(),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/members/humans",
            new AddUnitHumanMemberRequest(humanId, "owner",
                new[] { "from-cli" }, new[] { "escalation" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitHumanMemberResponse>(ct);
        body!.MembershipId.ShouldBe(installedMembershipId);
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
