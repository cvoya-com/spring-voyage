// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

public class DirectoryEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DirectoryEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListEntries_ReturnsAllDirectoryEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", "agent-1"), "actor-1", "Agent One", "First agent", "backend", DateTimeOffset.UtcNow),
            new(new Address("unit", "unit-1"), "actor-2", "Unit One", "First unit", null, DateTimeOffset.UtcNow)
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var response = await _client.GetAsync("/api/v1/tenant/directory", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<List<DirectoryEntryResponse>>(ct);
        result!.Count().ShouldBe(2);
        result![0].Address.Scheme.ShouldBe("agent");
        result[1].Address.Scheme.ShouldBe("unit");
    }

    [Fact]
    public async Task FindByRole_ReturnsMatchingEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", "agent-1"), "actor-1", "Agent One", "First agent", "backend", DateTimeOffset.UtcNow)
        };
        _factory.DirectoryService.ResolveByRoleAsync("backend", Arg.Any<CancellationToken>()).Returns(entries);

        var response = await _client.GetAsync("/api/v1/tenant/directory/role/backend", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<List<DirectoryEntryResponse>>(ct);
        result!.Count().ShouldBe(1);
        result![0].Role.ShouldBe("backend");
    }

    [Fact]
    public async Task Search_ForwardsQueryAndMapsHits()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ExpertiseSearch
            .SearchAsync(Arg.Any<ExpertiseSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ExpertiseSearchResult(
                new[]
                {
                    new ExpertiseSearchHit(
                        Slug: "python",
                        Domain: new ExpertiseDomain("python", "Python expertise", ExpertiseLevel.Advanced, "{\"type\":\"object\"}"),
                        Owner: new Address("unit", "eng"),
                        OwnerDisplayName: "Engineering",
                        AggregatingUnit: null,
                        TypedContract: true,
                        Score: 100,
                        MatchReason: "exact slug"),
                },
                TotalCount: 1,
                Limit: 50,
                Offset: 0));

        var request = new DirectorySearchRequest(
            Text: "python",
            TypedOnly: true);

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/directory/search", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<DirectorySearchResponse>(ct);
        result.ShouldNotBeNull();
        result.TotalCount.ShouldBe(1);
        result.Hits.Count.ShouldBe(1);
        result.Hits[0].Slug.ShouldBe("python");
        result.Hits[0].TypedContract.ShouldBeTrue();
    }

    [Fact]
    public async Task Search_CarriesAncestorChainAndProjectionPaths()
    {
        // #553: widened response surface. The host must forward the
        // ancestor chain + projection paths from the core search result
        // rather than dropping them.
        var ct = TestContext.Current.CancellationToken;
        _factory.ExpertiseSearch
            .SearchAsync(Arg.Any<ExpertiseSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ExpertiseSearchResult(
                new[]
                {
                    new ExpertiseSearchHit(
                        Slug: "translation",
                        Domain: new ExpertiseDomain("translation", "Translation expertise", ExpertiseLevel.Advanced, null),
                        Owner: new Address("unit", "origin"),
                        OwnerDisplayName: "Origin",
                        AggregatingUnit: new Address("unit", "root"),
                        TypedContract: false,
                        Score: 20,
                        MatchReason: "aggregated coverage",
                        AncestorChain: new[]
                        {
                            new Address("unit", "mid"),
                            new Address("unit", "root"),
                        },
                        ProjectionPaths: new[]
                        {
                            "projection/translation",
                            "projection/translation",
                        }),
                },
                TotalCount: 1,
                Limit: 50,
                Offset: 0));

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/directory/search",
            new DirectorySearchRequest(Text: "translation"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<DirectorySearchResponse>(ct);
        result.ShouldNotBeNull();
        var hit = result.Hits.ShouldHaveSingleItem();
        hit.AncestorChain.Count.ShouldBe(2);
        hit.AncestorChain[0].Scheme.ShouldBe("unit");
        hit.AncestorChain[0].Path.ShouldBe("mid");
        hit.AncestorChain[1].Path.ShouldBe("root");
        hit.ProjectionPaths.Count.ShouldBe(2);
        hit.ProjectionPaths.ShouldAllBe(p => p == "projection/translation");
    }

    [Fact]
    public async Task Search_DirectHit_EmitsEmptyChainAndProjectionPaths()
    {
        // #553: direct hits must surface empty collections (not null) so
        // generated clients don't have to null-check.
        var ct = TestContext.Current.CancellationToken;
        _factory.ExpertiseSearch
            .SearchAsync(Arg.Any<ExpertiseSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ExpertiseSearchResult(
                new[]
                {
                    new ExpertiseSearchHit(
                        Slug: "python",
                        Domain: new ExpertiseDomain("python", "Python expertise", ExpertiseLevel.Advanced, null),
                        Owner: new Address("unit", "eng"),
                        OwnerDisplayName: "Engineering",
                        AggregatingUnit: null,
                        TypedContract: false,
                        Score: 100,
                        MatchReason: "exact slug"),
                },
                TotalCount: 1,
                Limit: 50,
                Offset: 0));

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/directory/search",
            new DirectorySearchRequest(Text: "python"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<DirectorySearchResponse>(ct);
        result.ShouldNotBeNull();
        var hit = result.Hits.ShouldHaveSingleItem();
        hit.AncestorChain.ShouldNotBeNull();
        hit.AncestorChain.Count.ShouldBe(0);
        hit.ProjectionPaths.ShouldNotBeNull();
        hit.ProjectionPaths.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Search_NullBody_TreatedAsEmptyQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ExpertiseSearch
            .SearchAsync(Arg.Any<ExpertiseSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ExpertiseSearchResult(
                Array.Empty<ExpertiseSearchHit>(),
                TotalCount: 0,
                Limit: 50,
                Offset: 0));

        // Explicit null body to exercise the route's "null → empty query" path.
        var response = await _client.PostAsync("/api/v1/tenant/directory/search",
            new StringContent("null", System.Text.Encoding.UTF8, "application/json"), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}