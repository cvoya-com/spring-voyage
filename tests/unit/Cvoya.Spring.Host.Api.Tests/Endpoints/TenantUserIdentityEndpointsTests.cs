// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

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
    public async Task FindByAuthSubject_KnownSubject_Returns200_WithEnvelope()
    {
        // ADR-0062 § 6 / #2827: the CLI's `<tenant-user-ref>` parser
        // calls this endpoint when the operator passes a non-Guid,
        // non-`me` string (an OAuth subject). The route must scope to
        // the current tenant via the standard query filter.
        var ct = TestContext.Current.CancellationToken;
        var tenantUserId = Guid.NewGuid();
        var subject = $"alice-{Guid.NewGuid():N}@example.com";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.TenantUsers.Add(new TenantUserEntity
            {
                Id = tenantUserId,
                TenantId = OssTenantIds.Default,
                AuthSubject = subject,
                DisplayName = "Alice",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
        }

        var response = await _client.GetAsync(
            $"/api/v1/tenant/users/?authSubject={Uri.EscapeDataString(subject)}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TenantUserResponse>(ct);
        body.ShouldNotBeNull();
        body!.Id.ShouldBe(tenantUserId);
        body.AuthSubject.ShouldBe(subject);
        body.DisplayName.ShouldBe("Alice");
    }

    [Fact]
    public async Task FindByAuthSubject_UnknownSubject_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = $"nobody-{Guid.NewGuid():N}@example.com";

        var response = await _client.GetAsync(
            $"/api/v1/tenant/users/?authSubject={Uri.EscapeDataString(subject)}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FindByAuthSubject_MissingQueryParam_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/users/?authSubject=", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
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

    // ── #2808 / ADR-0062 § 2: PATCH /primary-human ──────────────────────

    [Fact]
    public async Task SetPrimaryHuman_BoundHat_PersistsAndReturnsTuple()
    {
        // ADR-0062 § 2: `spring user identity set-primary <human-ref>`
        // writes tenant_users.primary_human_id. The validation hinges on
        // the Hat being bound to the named TenantUser via the
        // humans.tenant_user_id FK — this test exercises the happy path
        // where the FK matches.
        var ct = TestContext.Current.CancellationToken;
        var tenantUserId = await SeedTenantUserAsync("alice");
        var humanId = await SeedHumanBoundToAsync(tenantUserId);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/users/{tenantUserId:D}/primary-human",
            new SetPrimaryHumanRequest(humanId),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SetPrimaryHumanResponse>(ct);
        body.ShouldNotBeNull();
        body!.TenantUserId.ShouldBe(tenantUserId);
        body.PrimaryHumanId.ShouldBe(humanId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.TenantUsers.SingleAsync(u => u.Id == tenantUserId, ct);
        row.PrimaryHumanId.ShouldBe(humanId);
    }

    [Fact]
    public async Task SetPrimaryHuman_UnboundHat_Returns400()
    {
        // The Hat exists in the tenant but is bound to a different
        // TenantUser. Per ADR-0062 § 2 this is the "speaking-as-someone-
        // -else's-Hat" case — the API rejects with a CLI-friendly 400 so
        // the operator gets a precise diagnostic instead of a silent FK
        // violation.
        var ct = TestContext.Current.CancellationToken;
        var ownerId = await SeedTenantUserAsync("owner");
        var contenderId = await SeedTenantUserAsync("contender");
        var humanBoundToOwner = await SeedHumanBoundToAsync(ownerId);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/users/{contenderId:D}/primary-human",
            new SetPrimaryHumanRequest(humanBoundToOwner),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("not bound");
    }

    [Fact]
    public async Task SetPrimaryHuman_UnknownTenantUser_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var ghost = Guid.NewGuid();

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/users/{ghost:D}/primary-human",
            new SetPrimaryHumanRequest(Guid.NewGuid()),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SetPrimaryHuman_EmptyHumanId_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantUserId = await SeedTenantUserAsync("alice");

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/users/{tenantUserId:D}/primary-human",
            new SetPrimaryHumanRequest(Guid.Empty),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<Guid> SeedHumanBoundToAsync(Guid tenantUserId)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var id = Guid.NewGuid();
        db.Humans.Add(new HumanEntity
        {
            Id = id,
            TenantId = OssTenantIds.Default,
            TenantUserId = tenantUserId,
            Username = $"hat-{id:N}",
            DisplayName = $"Hat {id:N}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return id;
    }

    // ── #2829: GET /me/humans surfaces disambiguatedLabel ─────────────────

    [Fact]
    public async Task ListCallerHumans_NoCollision_DisambiguatedLabelEqualsDisplayName()
    {
        // Baseline: a Hat with a unique display name across the
        // bound set finds no siblings; the disambiguatedLabel collapses
        // to the raw display name. We assert on a freshly-seeded Hat
        // with a Guid-derived name so it is guaranteed unique even
        // when sibling tests have planted other Hats on the same
        // shared operator TenantUser (the fixture is reused class-wide).
        var ct = TestContext.Current.CancellationToken;
        var uniqueName = $"UniqueHat-{Guid.NewGuid():N}";
        var unitId = await SeedUnitAsync("SoloUnit");
        var humanId = await SeedHumanWithMembershipAsync(
            OssTenantUserIds.Operator, uniqueName, unitId, new[] { "designer" });

        var response = await _client.GetAsync("/api/v1/tenant/users/me/humans", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<List<CallerHumanResponse>>(ct);
        body.ShouldNotBeNull();
        var row = body!.SingleOrDefault(r => r.HumanId == humanId);
        row.ShouldNotBeNull();
        row!.DisplayName.ShouldBe(uniqueName);
        row.DisambiguatedLabel.ShouldBe(uniqueName);
    }

    [Fact]
    public async Task ListCallerHumans_ScopedToRecipient_ReturnsOnlyWearableHats()
    {
        // #2972: GET /me/humans?recipient=<scheme:id> narrows the result to
        // the Hats that can reach the recipient so the messaging from-selector
        // never offers an unwearable Hat. The reachability decision is the
        // gate's; the endpoint just filters by it.
        var ct = TestContext.Current.CancellationToken;
        var unitA = await SeedUnitAsync("ReachUnitA");
        var unitB = await SeedUnitAsync("ReachUnitB");
        var hatA = await SeedHumanWithMembershipAsync(
            OssTenantUserIds.Operator, $"HatA-{Guid.NewGuid():N}", unitA, new[] { "designer" });
        var hatB = await SeedHumanWithMembershipAsync(
            OssTenantUserIds.Operator, $"HatB-{Guid.NewGuid():N}", unitB, new[] { "designer" });

        // Only hatA can reach unitA.
        _factory.HatReachability.GetWearableHatsAsync(
                Arg.Any<Guid>(),
                Arg.Is<IReadOnlyCollection<Address>>(t => t.Any(a => a.Id == unitA)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(new[] { hatA }));

        var response = await _client.GetAsync(
            $"/api/v1/tenant/users/me/humans?recipient=unit:{unitA:N}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<List<CallerHumanResponse>>(ct);
        body.ShouldNotBeNull();
        body!.ShouldContain(r => r.HumanId == hatA);
        body.ShouldNotContain(r => r.HumanId == hatB);
    }

    [Fact]
    public async Task ListCallerHumans_SameNameDifferentRoles_AppendsRole()
    {
        // Seed two extra Hats with the same display name bound to the
        // operator, both in the same unit but with different roles.
        // The endpoint should surface "<name> — designer" / "<name> —
        // reviewer" on the wire. Display name is per-test unique so a
        // sibling test that adds more colliding Hats can't perturb
        // the expected count.
        var ct = TestContext.Current.CancellationToken;
        var name = $"Bob-{Guid.NewGuid():N}";
        await SeedCollidingHatsAsync(
            tenantUserId: OssTenantUserIds.Operator,
            firstName: name, firstRole: "designer", firstUnitName: "Magazine",
            secondName: name, secondRole: "reviewer", secondUnitName: "Magazine");

        var response = await _client.GetAsync("/api/v1/tenant/users/me/humans", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<List<CallerHumanResponse>>(ct);
        body.ShouldNotBeNull();
        var rows = body!.Where(r => r.DisplayName == name).ToList();
        rows.Count.ShouldBe(2);
        rows.Select(r => r.DisambiguatedLabel)
            .ShouldBe(new[] { $"{name} — designer", $"{name} — reviewer" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ListCallerHumans_SameNameSameRoleDifferentUnits_AppendsUnit()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"Carol-{Guid.NewGuid():N}";
        await SeedCollidingHatsAsync(
            tenantUserId: OssTenantUserIds.Operator,
            firstName: name, firstRole: "designer", firstUnitName: "Magazine",
            secondName: name, secondRole: "designer", secondUnitName: "Newsletter");

        var response = await _client.GetAsync("/api/v1/tenant/users/me/humans", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<List<CallerHumanResponse>>(ct);
        body.ShouldNotBeNull();
        var rows = body!.Where(r => r.DisplayName == name).ToList();
        rows.Count.ShouldBe(2);
        rows.Select(r => r.DisambiguatedLabel)
            .ShouldBe(new[] { $"{name} (Magazine)", $"{name} (Newsletter)" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ListCallerHumans_SameNameSameRoleSameUnit_AppendsGuidSuffix()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"Diana-{Guid.NewGuid():N}";
        var unitId = await SeedUnitAsync("Magazine");
        var firstHumanId = await SeedHumanWithMembershipAsync(
            OssTenantUserIds.Operator, name, unitId, new[] { "designer" });
        var secondHumanId = await SeedHumanWithMembershipAsync(
            OssTenantUserIds.Operator, name, unitId, new[] { "designer" });

        var response = await _client.GetAsync("/api/v1/tenant/users/me/humans", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<List<CallerHumanResponse>>(ct);
        body.ShouldNotBeNull();
        var rows = body!.Where(r => r.DisplayName == name).ToList();
        rows.Count.ShouldBe(2);
        foreach (var row in rows)
        {
            row.DisambiguatedLabel.ShouldStartWith($"{name} #");
            row.DisambiguatedLabel.Length.ShouldBe($"{name} #".Length + 4);
            var suffix = row.HumanId.ToString("N")[..4];
            row.DisambiguatedLabel.ShouldBe($"{name} #{suffix}");
        }
        _ = firstHumanId;
        _ = secondHumanId;
    }

    /// <summary>
    /// Seeds two Humans bound to <paramref name="tenantUserId"/>, each
    /// with one unit membership carrying the supplied role. Used by the
    /// disambiguation tests to plant the collision tiers without a
    /// per-test rats-nest of EF setup. Returns the (first, second) ids.
    /// </summary>
    private async Task<(Guid First, Guid Second)> SeedCollidingHatsAsync(
        Guid tenantUserId,
        string firstName, string firstRole, string firstUnitName,
        string secondName, string secondRole, string secondUnitName)
    {
        var firstUnitId = await SeedUnitAsync(firstUnitName);
        var secondUnitId = firstUnitName == secondUnitName
            ? firstUnitId
            : await SeedUnitAsync(secondUnitName);

        var first = await SeedHumanWithMembershipAsync(
            tenantUserId, firstName, firstUnitId, new[] { firstRole });
        var second = await SeedHumanWithMembershipAsync(
            tenantUserId, secondName, secondUnitId, new[] { secondRole });
        return (first, second);
    }

    private async Task<Guid> SeedUnitAsync(string displayName)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var id = Guid.NewGuid();
        db.UnitDefinitions.Add(new Cvoya.Spring.Dapr.Data.Entities.UnitDefinitionEntity
        {
            Id = id,
            TenantId = OssTenantIds.Default,
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return id;
    }

    private async Task<Guid> SeedHumanWithMembershipAsync(
        Guid tenantUserId,
        string displayName,
        Guid unitId,
        IReadOnlyList<string> roles)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var humanId = Guid.NewGuid();
        db.Humans.Add(new HumanEntity
        {
            Id = humanId,
            TenantId = OssTenantIds.Default,
            TenantUserId = tenantUserId,
            Username = $"hat-{humanId:N}",
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.UnitMembershipsHumans.Add(new Cvoya.Spring.Dapr.Data.Entities.UnitMembershipHumanEntity
        {
            Id = Guid.NewGuid(),
            TenantId = OssTenantIds.Default,
            UnitId = unitId,
            HumanId = humanId,
            Roles = new List<string>(roles),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return humanId;
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
