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
/// REST-level tests for the human ↔ connector identity endpoints (#2408).
/// Covers the upsert / list / delete contract end-to-end through the
/// minimal-API stack, plus the unique-index conflict that surfaces as 409.
/// </summary>
public class HumanIdentityEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public HumanIdentityEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Upsert_NewIdentity_Returns200_PersistsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var humanId = await SeedHumanAsync("alice");
        var login = NewLogin();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/humans/{humanId:N}/identities",
            new HumanConnectorIdentityRequest("github", login, "Alice"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HumanConnectorIdentityResponse>(ct);
        body.ShouldNotBeNull();
        body!.HumanId.ShouldBe(humanId);
        body.ConnectorId.ShouldBe("github");
        body.ConnectorUserId.ShouldBe(login);
        body.DisplayHandle.ShouldBe("Alice");

        // Persisted in EF.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.HumanConnectorIdentities
            .AsNoTracking()
            .SingleAsync(e => e.HumanId == humanId && e.ConnectorUserId == login, ct);
        row.ConnectorId.ShouldBe("github");
    }

    [Fact]
    public async Task Upsert_SameTuple_UpdatesDisplayHandle()
    {
        var ct = TestContext.Current.CancellationToken;
        var humanId = await SeedHumanAsync("alice");
        var login = NewLogin();

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/humans/{humanId:N}/identities",
            new HumanConnectorIdentityRequest("github", login, "old"),
            ct);
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/humans/{humanId:N}/identities",
            new HumanConnectorIdentityRequest("github", login, "new"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HumanConnectorIdentityResponse>(ct);
        body!.DisplayHandle.ShouldBe("new");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = await db.HumanConnectorIdentities
            .AsNoTracking()
            .Where(e => e.HumanId == humanId && e.ConnectorUserId == login)
            .ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].DisplayHandle.ShouldBe("new");
    }

    [Fact]
    public async Task Upsert_AnotherHumanOwnsTuple_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        var ownerHumanId = await SeedHumanAsync("owner");
        var contenderHumanId = await SeedHumanAsync("contender");
        var login = NewLogin();

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/humans/{ownerHumanId:N}/identities",
            new HumanConnectorIdentityRequest("github", login, null),
            ct);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/humans/{contenderHumanId:N}/identities",
            new HumanConnectorIdentityRequest("github", login, null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Upsert_UnknownHuman_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/humans/{Guid.NewGuid():N}/identities",
            new HumanConnectorIdentityRequest("github", NewLogin(), null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upsert_MissingFields_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var humanId = await SeedHumanAsync("alice");

        var blankConnector = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/humans/{humanId:N}/identities",
            new HumanConnectorIdentityRequest(string.Empty, "alice", null),
            ct);
        blankConnector.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var blankUser = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/humans/{humanId:N}/identities",
            new HumanConnectorIdentityRequest("github", string.Empty, null),
            ct);
        blankUser.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_ReturnsOnlyThisHumanIdentities()
    {
        var ct = TestContext.Current.CancellationToken;
        var humanId = await SeedHumanAsync("alice");
        var otherHumanId = await SeedHumanAsync("bob");
        var aliceGithub = NewLogin();
        var aliceSlack = NewLogin();
        var bobGithub = NewLogin();

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/humans/{humanId:N}/identities",
            new HumanConnectorIdentityRequest("github", aliceGithub, null), ct);
        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/humans/{humanId:N}/identities",
            new HumanConnectorIdentityRequest("slack", aliceSlack, null), ct);
        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/humans/{otherHumanId:N}/identities",
            new HumanConnectorIdentityRequest("github", bobGithub, null), ct);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/humans/{humanId:N}/identities", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var rows = await response.Content.ReadFromJsonAsync<List<HumanConnectorIdentityResponse>>(ct);
        rows.ShouldNotBeNull();
        rows!.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.HumanId == humanId);
        rows.Select(r => r.ConnectorId).ShouldBe(new[] { "github", "slack" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Delete_ExistingRow_Returns204_RemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var humanId = await SeedHumanAsync("alice");
        var login = NewLogin();

        await _client.PostAsJsonAsync(
            $"/api/v1/tenant/humans/{humanId:N}/identities",
            new HumanConnectorIdentityRequest("github", login, null), ct);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/humans/{humanId:N}/identities?connectorId=github&connectorUserId={login}",
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        (await db.HumanConnectorIdentities.AsNoTracking().AnyAsync(
            e => e.HumanId == humanId && e.ConnectorUserId == login, ct))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_NoSuchRow_Returns204_Idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var humanId = await SeedHumanAsync("alice");

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/humans/{humanId:N}/identities?connectorId=github&connectorUserId=missing-{Guid.NewGuid():N}",
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_MissingQueryParams_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var humanId = await SeedHumanAsync("alice");

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/humans/{humanId:N}/identities", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static string NewLogin() => $"login-{Guid.NewGuid():N}";

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
