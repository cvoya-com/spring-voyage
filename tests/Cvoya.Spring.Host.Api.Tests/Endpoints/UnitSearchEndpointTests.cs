// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// #1649: server-side search filters on <c>GET /api/v1/tenant/units</c>.
/// Mirrors the agents-list filter tests in <c>AgentEndpointsTests</c>;
/// these exercise the unit-side <c>?display_name=</c> + <c>?parent_id=</c>
/// query params so the CLI's <c>unit show &lt;name&gt; --unit &lt;parent&gt;</c>
/// resolver can collapse to one round-trip per call.
/// </summary>
public class UnitSearchEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    // Server serialises enums as strings (Program.cs#134); tests must match.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Guid ParentEngineering = new("ee1ee111-aaaa-0000-0000-000000000001");

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitSearchEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListUnits_DisplayNameFilter_NoMatch_ReturnsEmptyArray()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearSubunitEdges();
        SeedThreeUnitsAndOneParent();

        var response = await _client.GetAsync(
            "/api/v1/tenant/units?display_name=ghost", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var units = await response.Content.ReadFromJsonAsync<List<UnitResponse>>(JsonOptions, ct);
        units.ShouldNotBeNull();
        units.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListUnits_DisplayNameFilter_OneMatch_ReturnsSingleUnit()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearSubunitEdges();
        SeedThreeUnitsAndOneParent();

        var response = await _client.GetAsync(
            "/api/v1/tenant/units?display_name=Backend", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var units = await response.Content.ReadFromJsonAsync<List<UnitResponse>>(JsonOptions, ct);
        units.ShouldNotBeNull();
        units.Count.ShouldBe(1);
        units[0].DisplayName.ShouldBe("Backend");
    }

    [Fact]
    public async Task ListUnits_DisplayNameFilter_CaseInsensitive_ReturnsMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearSubunitEdges();
        SeedThreeUnitsAndOneParent();

        var response = await _client.GetAsync(
            "/api/v1/tenant/units?display_name=BACKEND", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var units = await response.Content.ReadFromJsonAsync<List<UnitResponse>>(JsonOptions, ct);
        units.ShouldNotBeNull();
        units.Count.ShouldBe(1);
        units[0].DisplayName.ShouldBe("Backend");
    }

    [Fact]
    public async Task ListUnits_DisplayNameFilter_MultipleMatches_ReturnsAll()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearSubunitEdges();
        SeedUnitsWithDuplicateDisplayName();

        var response = await _client.GetAsync(
            "/api/v1/tenant/units?display_name=Backend", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var units = await response.Content.ReadFromJsonAsync<List<UnitResponse>>(JsonOptions, ct);
        units.ShouldNotBeNull();
        units.Count.ShouldBe(2);
        units.ShouldAllBe(u => u.DisplayName == "Backend");
    }

    [Fact]
    public async Task ListUnits_ParentIdFilter_NarrowsToDirectChildren()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearSubunitEdges();
        var (backend, frontend, _) = SeedThreeUnitsAndOneParent();

        // Only Backend is a direct child of the Engineering parent.
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IUnitSubunitMembershipRepository>();
            await repo.UpsertAsync(ParentEngineering, backend, ct);
        }

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units?parent_id={ParentEngineering:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var units = await response.Content.ReadFromJsonAsync<List<UnitResponse>>(JsonOptions, ct);
        units.ShouldNotBeNull();
        units.Count.ShouldBe(1);
        units[0].Id.ShouldBe(backend);
    }

    [Fact]
    public async Task ListUnits_DisplayNameAndParentIdFilters_Compose()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearSubunitEdges();
        var (backend, frontend, _) = SeedThreeUnitsAndOneParent();

        // Both Backend and Frontend are children of Engineering, but
        // display_name=Backend narrows to one.
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IUnitSubunitMembershipRepository>();
            await repo.UpsertAsync(ParentEngineering, backend, ct);
            await repo.UpsertAsync(ParentEngineering, frontend, ct);
        }

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units?display_name=Backend&parent_id={ParentEngineering:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var units = await response.Content.ReadFromJsonAsync<List<UnitResponse>>(JsonOptions, ct);
        units.ShouldNotBeNull();
        units.Count.ShouldBe(1);
        units[0].Id.ShouldBe(backend);
        units[0].DisplayName.ShouldBe("Backend");
    }

    [Fact]
    public async Task ListUnits_ParentIdFilter_NoChildren_ReturnsEmptyArray()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearSubunitEdges();
        SeedThreeUnitsAndOneParent();

        // No subunit edges seeded ⇒ engineering parent has zero children.
        var response = await _client.GetAsync(
            $"/api/v1/tenant/units?parent_id={ParentEngineering:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var units = await response.Content.ReadFromJsonAsync<List<UnitResponse>>(JsonOptions, ct);
        units.ShouldNotBeNull();
        units.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListUnits_MalformedParentId_ReturnsEmptyArray()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearSubunitEdges();
        SeedThreeUnitsAndOneParent();

        var response = await _client.GetAsync(
            "/api/v1/tenant/units?parent_id=not-a-guid", ct);

        // Mirrors the agents-side stance: a malformed parent_id is treated
        // as "no match" rather than 400 — the empty result is the canonical
        // "no matches" wire shape and the CLI never sends a malformed id.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var units = await response.Content.ReadFromJsonAsync<List<UnitResponse>>(JsonOptions, ct);
        units.ShouldNotBeNull();
        units.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListUnits_NoFilters_ReturnsAllUnits()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearSubunitEdges();
        SeedThreeUnitsAndOneParent();

        var response = await _client.GetAsync("/api/v1/tenant/units", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var units = await response.Content.ReadFromJsonAsync<List<UnitResponse>>(JsonOptions, ct);
        units.ShouldNotBeNull();
        // 3 children + 1 parent (all are 'unit' scheme entries).
        units.Count.ShouldBe(4);
    }

    /// <summary>
    /// Seeds the directory mock with three child units (Backend / Frontend /
    /// Marketing) and the engineering parent. Returns each child's Guid
    /// so individual tests can wire subunit edges through the real EF repo.
    /// </summary>
    private (Guid backend, Guid frontend, Guid marketing) SeedThreeUnitsAndOneParent()
    {
        var backend = Guid.NewGuid();
        var frontend = Guid.NewGuid();
        var marketing = Guid.NewGuid();

        var entries = new List<DirectoryEntry>
        {
            new(new Address("unit", backend), backend, "Backend", "be", null, DateTimeOffset.UtcNow),
            new(new Address("unit", frontend), frontend, "Frontend", "fe", null, DateTimeOffset.UtcNow),
            new(new Address("unit", marketing), marketing, "Marketing", "mk", null, DateTimeOffset.UtcNow),
            new(new Address("unit", ParentEngineering), ParentEngineering, "Engineering", "eng", null, DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(entries);

        return (backend, frontend, marketing);
    }

    /// <summary>
    /// Seeds two units with the same display_name "Backend" — used to
    /// verify the n-match path returns the full candidate list.
    /// </summary>
    private void SeedUnitsWithDuplicateDisplayName()
    {
        var backendOne = Guid.NewGuid();
        var backendTwo = Guid.NewGuid();
        var marketing = Guid.NewGuid();

        var entries = new List<DirectoryEntry>
        {
            new(new Address("unit", backendOne), backendOne, "Backend", "be1", null, DateTimeOffset.UtcNow),
            new(new Address("unit", backendTwo), backendTwo, "Backend", "be2", null, DateTimeOffset.UtcNow),
            new(new Address("unit", marketing), marketing, "Marketing", "mk", null, DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(entries);
    }

    private void ClearSubunitEdges()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Cvoya.Spring.Dapr.Data.SpringDbContext>();
        ctx.UnitSubunitMemberships.RemoveRange(ctx.UnitSubunitMemberships.ToList());
        ctx.SaveChanges();
    }
}
