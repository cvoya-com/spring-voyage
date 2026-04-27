// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Semantic contract tests for the platform-tenants surface (#1260 / C1.2d).
/// Companion to the behavioural <c>PlatformTenantsEndpointsTests</c> — those
/// check what the endpoint does; these check that response bodies match the
/// committed openapi.json.
/// </summary>
public class PlatformTenantsContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PlatformTenantsContractTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListTenants_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantAsync("contract-list-default", ct);

        var response = await _client.GetAsync("/api/v1/platform/tenants", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/platform/tenants", "get", "200", body);
    }

    [Fact]
    public async Task CreateTenant_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/platform/tenants",
            new CreateTenantRequest("contract-create", "Contract Create"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/platform/tenants", "post", "201", body);
    }

    [Fact]
    public async Task GetTenant_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantAsync("contract-get", ct);

        var response = await _client.GetAsync("/api/v1/platform/tenants/contract-get", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/platform/tenants/{id}", "get", "200", body);
    }

    [Fact]
    public async Task GetTenant_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/platform/tenants/contract-ghost", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/platform/tenants/{id}", "get", "404", body, "application/problem+json");
    }

    private async Task SeedTenantAsync(string id, CancellationToken cancellationToken)
    {
        using var scope = _factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ITenantRegistry>();
        if (await registry.GetAsync(id, cancellationToken) is null)
        {
            await registry.CreateAsync(id, $"display-{id}", cancellationToken);
        }
    }
}