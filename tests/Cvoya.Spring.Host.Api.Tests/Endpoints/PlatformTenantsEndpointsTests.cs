// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <c>/api/v1/platform/tenants</c> (#1260 / C1.2d).
/// </summary>
public class PlatformTenantsEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    /// <summary>
    /// Mirrors the host's outbound configuration so tests can read enum
    /// values (<see cref="TenantState"/>) that the host serialises as
    /// JSON strings via <c>JsonStringEnumConverter</c>.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public PlatformTenantsEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListTenants_DefaultTenantSeeded_ReturnsOk()
    {
        var ct = TestContext.Current.CancellationToken;

        // Plant the default tenant via the registry directly so the test
        // exercises the read path against a known shape independent of
        // bootstrap timing.
        await SeedDefaultTenantAsync(ct);

        var response = await _client.GetAsync("/api/v1/platform/tenants", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<TenantsListResponse>(JsonOptions, ct);
        payload.ShouldNotBeNull();
        payload.Items.ShouldNotBeNull();
        payload.Items.ShouldContain(t => t.Id == "default" && t.State == TenantState.Active);
    }

    [Fact]
    public async Task CreateTenant_HappyPath_ReturnsCreated()
    {
        var ct = TestContext.Current.CancellationToken;

        var request = new CreateTenantRequest("acme", "ACME Corporation");
        var response = await _client.PostAsJsonAsync("/api/v1/platform/tenants", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location?.ToString().ShouldBe("/api/v1/platform/tenants/acme");

        var payload = await response.Content.ReadFromJsonAsync<TenantResponse>(JsonOptions, ct);
        payload.ShouldNotBeNull();
        payload.Id.ShouldBe("acme");
        payload.DisplayName.ShouldBe("ACME Corporation");
        payload.State.ShouldBe(TenantState.Active);

        // GET should now resolve.
        var getResponse = await _client.GetAsync("/api/v1/platform/tenants/acme", ct);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateTenant_DuplicateId_ReturnsConflict()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await _client.PostAsJsonAsync(
            "/api/v1/platform/tenants",
            new CreateTenantRequest("dup-tenant", null),
            ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        var second = await _client.PostAsJsonAsync(
            "/api/v1/platform/tenants",
            new CreateTenantRequest("dup-tenant", null),
            ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateTenant_MalformedId_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;

        // Upper-case ids violate the registry's slug shape.
        var response = await _client.PostAsJsonAsync(
            "/api/v1/platform/tenants",
            new CreateTenantRequest("BadCaseTenant", null),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTenant_EmptyId_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/platform/tenants",
            new CreateTenantRequest("", null),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetTenant_Unknown_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/platform/tenants/ghost-tenant", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateTenant_HappyPath_UpdatesDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;

        var create = await _client.PostAsJsonAsync(
            "/api/v1/platform/tenants",
            new CreateTenantRequest("upd-tenant", "Original"),
            ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Created);

        var patch = await _client.PatchAsJsonAsync(
            "/api/v1/platform/tenants/upd-tenant",
            new UpdateTenantRequest("Updated"),
            ct);
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);

        var payload = await patch.Content.ReadFromJsonAsync<TenantResponse>(JsonOptions, ct);
        payload.ShouldNotBeNull();
        payload.DisplayName.ShouldBe("Updated");
    }

    [Fact]
    public async Task UpdateTenant_Unknown_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PatchAsJsonAsync(
            "/api/v1/platform/tenants/ghost-update",
            new UpdateTenantRequest("Doesn't matter"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteTenant_HappyPath_ReturnsNoContent()
    {
        var ct = TestContext.Current.CancellationToken;

        var create = await _client.PostAsJsonAsync(
            "/api/v1/platform/tenants",
            new CreateTenantRequest("del-tenant", null),
            ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Created);

        var del = await _client.DeleteAsync("/api/v1/platform/tenants/del-tenant", ct);
        del.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Subsequent GET should 404 — soft-deleted rows are excluded
        // from the default view.
        var get = await _client.GetAsync("/api/v1/platform/tenants/del-tenant", ct);
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteTenant_Unknown_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.DeleteAsync("/api/v1/platform/tenants/ghost-delete", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AllRoutes_CallerWithoutPlatformOperator_Return403()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var restrictedFactory = BuildFactoryWithoutPlatformOperator();
        var restrictedClient = restrictedFactory.CreateClient();

        var routes = new (HttpMethod method, string url, HttpContent? body)[]
        {
            (HttpMethod.Get, "/api/v1/platform/tenants", null),
            (HttpMethod.Get, "/api/v1/platform/tenants/some-tenant", null),
            (HttpMethod.Post, "/api/v1/platform/tenants",
                JsonContent.Create(new CreateTenantRequest("blocked", null))),
            (HttpMethod.Patch, "/api/v1/platform/tenants/some-tenant",
                JsonContent.Create(new UpdateTenantRequest("Blocked"))),
            (HttpMethod.Delete, "/api/v1/platform/tenants/some-tenant", null),
        };

        foreach (var (method, url, body) in routes)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body };
            var response = await restrictedClient.SendAsync(request, ct);
            response.StatusCode.ShouldBe(
                HttpStatusCode.Forbidden,
                $"{method} {url} should 403 when the caller lacks PlatformOperator");
        }
    }

    private async Task SeedDefaultTenantAsync(CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ITenantRegistry>();
        var existing = await registry.GetAsync("default", ct);
        if (existing is null)
        {
            await registry.CreateAsync("default", "Default", ct);
        }
    }

    /// <summary>
    /// Spins up a separate factory with an <see cref="IRoleClaimSource"/>
    /// stub that emits no <see cref="PlatformRoles.PlatformOperator"/>
    /// claim. The OSS default grants every authenticated caller all three
    /// roles, so we have to pre-empt that registration to exercise the
    /// 403 path on the platform-tenant endpoints.
    /// </summary>
    private static WebApplicationFactory<Program> BuildFactoryWithoutPlatformOperator()
    {
        return new CustomWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    var existing = services
                        .Where(d => d.ServiceType == typeof(IRoleClaimSource))
                        .ToList();
                    foreach (var d in existing)
                    {
                        services.Remove(d);
                    }
                    services.TryAddSingleton<IRoleClaimSource, NonPlatformOperatorClaimSource>();
                });
            });
    }

    private sealed class NonPlatformOperatorClaimSource : IRoleClaimSource
    {
        public IEnumerable<Claim> GetRoleClaims(ClaimsIdentity identity)
        {
            // Grant the other two roles only — the missing PlatformOperator
            // is the gate the routes enforce.
            yield return new Claim(ClaimTypes.Role, PlatformRoles.TenantOperator);
            yield return new Claim(ClaimTypes.Role, PlatformRoles.TenantUser);
        }
    }
}