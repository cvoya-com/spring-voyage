// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// REST-level tests for the relocated tenant-user identity endpoints
/// (ADR-0047 §§ 2, 14). Covers the upsert / list / delete contract
/// end-to-end through the minimal-API stack, the unique-index conflict
/// path (409), the envelope GET / PATCH surface, and 410 Gone on the
/// retired <c>/api/v1/tenant/humans/{id}/identities</c> routes.
/// </summary>
public class TenantUserIdentityEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TenantUserIdentityEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Upsert_NewIdentity_Returns200_PersistsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantUserId = await SeedTenantUserAsync("alice");
        var login = NewLogin();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}/identities",
            new TenantUserConnectorIdentityRequest("github", login, "Alice"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TenantUserConnectorIdentityResponse>(ct);
        body.ShouldNotBeNull();
        body!.TenantUserId.ShouldBe(tenantUserId);
        body.ConnectorId.ShouldBe("github");
        body.Username.ShouldBe(login);
        body.DisplayHandle.ShouldBe("Alice");

        // Persisted in EF.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.TenantUserConnectorIdentities
            .AsNoTracking()
            .SingleAsync(e => e.TenantUserId == tenantUserId && e.Username == login, ct);
        row.ConnectorId.ShouldBe("github");
    }

    [Fact]
    public async Task Upsert_SamePair_UpdatesUsernameAndHandle()
    {
        // ADR-0047 §2: the natural key is (tenant, tenant_user, connector).
        // Re-running with a different username on the same connector
        // upserts in place rather than creating a second row — that's the
        // shape Phase F's OAuth-completion path relies on to refresh the
        // calling tenant user's GitHub login.
        var ct = TestContext.Current.CancellationToken;
        var tenantUserId = await SeedTenantUserAsync("alice");
        var oldLogin = NewLogin();
        var newLogin = NewLogin();

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}/identities",
            new TenantUserConnectorIdentityRequest("github", oldLogin, "old-display"),
            ct);
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}/identities",
            new TenantUserConnectorIdentityRequest("github", newLogin, "new-display"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = await db.TenantUserConnectorIdentities
            .AsNoTracking()
            .Where(e => e.TenantUserId == tenantUserId && e.ConnectorId == "github")
            .ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].Username.ShouldBe(newLogin);
        rows[0].DisplayHandle.ShouldBe("new-display");
    }

    [Fact]
    public async Task Upsert_AnotherTenantUserOwnsLogin_Returns409()
    {
        // ADR-0047 §2 reverse-lookup unique index — one connector login
        // maps to at most one tenant user per tenant. Including
        // tenant_user_id in the constraint would defeat the resolver's
        // "given a login, who is this?" query.
        var ct = TestContext.Current.CancellationToken;
        var ownerId = await SeedTenantUserAsync("owner");
        var contenderId = await SeedTenantUserAsync("contender");
        var login = NewLogin();

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/users/{ownerId:N}/identities",
            new TenantUserConnectorIdentityRequest("github", login, null),
            ct);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/users/{contenderId:N}/identities",
            new TenantUserConnectorIdentityRequest("github", login, null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Upsert_UnknownTenantUser_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/users/{Guid.NewGuid():N}/identities",
            new TenantUserConnectorIdentityRequest("github", NewLogin(), null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upsert_MissingFields_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantUserId = await SeedTenantUserAsync("alice");

        var blankConnector = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}/identities",
            new TenantUserConnectorIdentityRequest(string.Empty, "alice", null),
            ct);
        blankConnector.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var blankUser = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}/identities",
            new TenantUserConnectorIdentityRequest("github", string.Empty, null),
            ct);
        blankUser.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_ReturnsOnlyThisTenantUserIdentities()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantUserId = await SeedTenantUserAsync("alice");
        var otherId = await SeedTenantUserAsync("bob");
        var aliceGithub = NewLogin();
        var aliceSlack = NewLogin();
        var bobGithub = NewLogin();

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}/identities",
            new TenantUserConnectorIdentityRequest("github", aliceGithub, null), ct);
        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}/identities",
            new TenantUserConnectorIdentityRequest("slack", aliceSlack, null), ct);
        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/users/{otherId:N}/identities",
            new TenantUserConnectorIdentityRequest("github", bobGithub, null), ct);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}/identities", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var rows = await response.Content.ReadFromJsonAsync<List<TenantUserConnectorIdentityResponse>>(ct);
        rows.ShouldNotBeNull();
        rows!.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.TenantUserId == tenantUserId);
        rows.Select(r => r.ConnectorId).ShouldBe(new[] { "github", "slack" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Delete_ExistingRow_Returns204_RemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantUserId = await SeedTenantUserAsync("alice");
        var login = NewLogin();

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}/identities",
            new TenantUserConnectorIdentityRequest("github", login, null), ct);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}/identities?connectorId=github&username={login}",
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        (await db.TenantUserConnectorIdentities.AsNoTracking().AnyAsync(
            e => e.TenantUserId == tenantUserId && e.Username == login, ct))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_NoSuchRow_Returns204_Idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantUserId = await SeedTenantUserAsync("alice");

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}/identities?connectorId=github&username=missing-{Guid.NewGuid():N}",
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_MissingQueryParams_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantUserId = await SeedTenantUserAsync("alice");

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}/identities", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetTenantUser_ExistingRow_Returns200_WithEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantUserId = Guid.NewGuid();
        var seededAt = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.TenantUsers.Add(new TenantUserEntity
            {
                Id = tenantUserId,
                TenantId = OssTenantIds.Default,
                AuthSubject = $"sub-{tenantUserId:N}",
                DisplayName = "Alice",
                Description = "the human in the loop",
                CreatedAt = seededAt,
                UpdatedAt = seededAt,
            });
            await db.SaveChangesAsync(ct);
        }

        var response = await _client.GetAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TenantUserResponse>(ct);
        body.ShouldNotBeNull();
        body!.Id.ShouldBe(tenantUserId);
        body.AuthSubject.ShouldBe($"sub-{tenantUserId:N}");
        body.DisplayName.ShouldBe("Alice");
        body.Description.ShouldBe("the human in the loop");
        (body.CreatedAt - seededAt).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetTenantUser_UnknownId_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var unknown = Guid.NewGuid();
        var response = await _client.GetAsync(
            $"/api/v1/tenant/users/{unknown:N}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchTenantUser_UpdatesEditableFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantUserId = await SeedTenantUserAsync("alice");

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/users/{tenantUserId:N}",
            new UpdateTenantUserRequest(DisplayName: "Alice Updated", Description: "rewritten"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TenantUserResponse>(ct);
        body.ShouldNotBeNull();
        body!.DisplayName.ShouldBe("Alice Updated");
        body.Description.ShouldBe("rewritten");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task RetiredHumanIdentitiesRoute_Returns410WithMigrationHint(string method)
    {
        // ADR-0047 §14: the /api/v1/tenant/humans/{id}/identities routes
        // are retired with a 410 Gone stub that points callers at the new
        // path — the uniform contract for retired HTTP surfaces.
        var ct = TestContext.Current.CancellationToken;
        var humanId = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            $"/api/v1/tenant/humans/{humanId}/identities");

        using var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);

        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("HumanIdentityEndpointsRetired");
        body.ShouldContain("/api/v1/tenant/users");
    }

    private static string NewLogin() => $"login-{Guid.NewGuid():N}";

    private async Task<Guid> SeedTenantUserAsync(string displayName)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.TenantUsers.Add(new TenantUserEntity
        {
            Id = id,
            TenantId = OssTenantIds.Default,
            AuthSubject = $"sub-{id:N}",
            DisplayName = displayName,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return id;
    }
}
