// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// REST-level tests for the per-human envelope endpoints at
/// <c>/api/v1/tenant/humans/{humanId}</c>. The GET / PATCH surfaces are
/// covered indirectly elsewhere; this file pins the DELETE contract added
/// by #2649 — the human row, every <c>unit_membership_humans</c>
/// participation, and every <c>unit_human_permissions</c> grant must
/// disappear in the same write so the human is removed as a member from
/// every parent unit.
/// </summary>
public class HumanEnvelopeEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public HumanEnvelopeEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task DeleteHuman_HappyPath_RemovesRowAndReturns204()
    {
        var ct = TestContext.Current.CancellationToken;
        var humanId = await SeedHumanAsync("ada");

        var response = await _client.DeleteAsync($"/api/v1/tenant/humans/{humanId:D}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        (await db.Humans.AnyAsync(h => h.Id == humanId, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteHuman_CascadesEveryMembershipRow()
    {
        // #2649: a human delete must remove the human from every parent
        // unit's team-membership rows in the same write.
        var ct = TestContext.Current.CancellationToken;
        var humanId = await SeedHumanAsync("ada");
        var parentA = Guid.NewGuid();
        var parentB = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitMembershipsHumans.Add(new UnitMembershipHumanEntity
            {
                Id = Guid.NewGuid(),
                TenantId = OssTenantIds.Default,
                UnitId = parentA,
                HumanId = humanId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.UnitMembershipsHumans.Add(new UnitMembershipHumanEntity
            {
                Id = Guid.NewGuid(),
                TenantId = OssTenantIds.Default,
                UnitId = parentB,
                HumanId = humanId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var response = await _client.DeleteAsync($"/api/v1/tenant/humans/{humanId:D}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var remaining = await verifyDb.UnitMembershipsHumans
            .Where(m => m.HumanId == humanId)
            .CountAsync(ct);
        remaining.ShouldBe(0);
    }

    [Fact]
    public async Task DeleteHuman_CascadesEveryPermissionGrant()
    {
        // ACL grants on the sibling unit_human_permissions table also
        // reference HumanId — leaving them behind after delete would
        // surface as orphaned permission rows on the next ACL read.
        var ct = TestContext.Current.CancellationToken;
        var humanId = await SeedHumanAsync("ada");
        var unitA = Guid.NewGuid();
        var unitB = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitHumanPermissions.Add(new UnitHumanPermissionEntity
            {
                Id = Guid.NewGuid(),
                TenantId = OssTenantIds.Default,
                UnitId = unitA,
                HumanId = humanId,
                PermissionLevel = PermissionLevel.Owner,
                Identity = "ada",
                Notifications = true,
                GrantedAt = DateTimeOffset.UtcNow,
            });
            db.UnitHumanPermissions.Add(new UnitHumanPermissionEntity
            {
                Id = Guid.NewGuid(),
                TenantId = OssTenantIds.Default,
                UnitId = unitB,
                HumanId = humanId,
                PermissionLevel = PermissionLevel.Operator,
                Identity = "ada",
                Notifications = true,
                GrantedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var response = await _client.DeleteAsync($"/api/v1/tenant/humans/{humanId:D}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var remaining = await verifyDb.UnitHumanPermissions
            .Where(p => p.HumanId == humanId)
            .CountAsync(ct);
        remaining.ShouldBe(0);
    }

    [Fact]
    public async Task DeleteHuman_UnknownId_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var ghost = Guid.NewGuid();

        var response = await _client.DeleteAsync($"/api/v1/tenant/humans/{ghost:D}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteHuman_EmptyId_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.DeleteAsync($"/api/v1/tenant/humans/{Guid.Empty:D}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
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
            Username = username,
            DisplayName = username,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return id;
    }
}
