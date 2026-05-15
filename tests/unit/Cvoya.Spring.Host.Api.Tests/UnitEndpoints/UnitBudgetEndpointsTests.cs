// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.UnitEndpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Authorisation tests for the unit-keyed budget endpoints
/// (<c>GET / PUT /api/v1/tenant/units/{id}/budget</c>) introduced in
/// #2280. Without <c>LocalDev</c> the host runs <c>ApiTokenScheme</c>
/// and a missing token must 401 before any handler logic executes.
/// </summary>
/// <remarks>
/// The happy-path, 404, validation, and tenant-isolation cases for
/// these routes already live alongside the agent/tenant budget tests
/// in <see cref="BudgetEndpointsTests"/> (search for
/// <c>SetUnitBudget_*</c> / <c>GetUnitBudget_*</c>) and are not
/// duplicated under this folder. This file pins the authentication
/// contract — the auth-gate regression closed by #2288.
/// </remarks>
public class UnitBudgetEndpointsUnauthenticatedTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public UnitBudgetEndpointsUnauthenticatedTests()
    {
        var dbName = $"BudgetAuthTestDb_{Guid.NewGuid()}";
        var directoryService = Substitute.For<IDirectoryService>();
        var actorProxyFactory = Substitute.For<IActorProxyFactory>();
        var agentProxyResolver = Substitute.For<IAgentProxyResolver>();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // No LocalDev setting — the host picks ApiTokenScheme so
                // any route gated by .RequireAuthorization() rejects with
                // 401 before its handler runs.
                builder.UseSetting("ConnectionStrings:SpringDb",
                    "Host=test;Database=test;Username=test;Password=test");
                builder.ConfigureServices(services =>
                {
                    UnauthenticatedTestHostHelpers.ReplaceDbAndRuntime(
                        services,
                        dbName,
                        directoryService,
                        actorProxyFactory,
                        agentProxyResolver);
                });
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetBudget_Unauthenticated_Returns401()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Act — no Authorization header, no LocalDev shortcut.
        var response = await client.GetAsync(
            $"/api/v1/tenant/units/{Guid.NewGuid():N}/budget", ct);

        // Assert — the role gate on the unit-budget group rejects with
        // 401 before the handler runs. Pre-#2288 this surfaced as 404
        // because MapBudgetEndpoints only returned the agent group, so
        // the auth chain in Program.cs never reached the unit-scoped
        // group.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetBudget_Unauthenticated_Returns401()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Act — valid body so a missing gate would have let the EF
        // upsert run cleanly. The new gate must short-circuit here.
        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{Guid.NewGuid():N}/budget",
            new SetBudgetRequest(25.50m),
            ct);

        // Assert — pre-#2288 this returned 200 (handler reached). The
        // gate now rejects with 401 before the body is processed.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// Authorisation tests for the tenant-scope budget endpoints
/// (<c>GET / PUT /api/v1/tenant/budget</c>). Like the unit-scope
/// suite above this pins the auth-gate regression closed by #2288 —
/// the tenant-scope group also lived behind no gate because
/// <c>MapBudgetEndpoints</c> only returned the agent group.
/// </summary>
public class TenantBudgetEndpointsUnauthenticatedTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public TenantBudgetEndpointsUnauthenticatedTests()
    {
        var dbName = $"TenantBudgetAuthTestDb_{Guid.NewGuid()}";
        var directoryService = Substitute.For<IDirectoryService>();
        var actorProxyFactory = Substitute.For<IActorProxyFactory>();
        var agentProxyResolver = Substitute.For<IAgentProxyResolver>();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:SpringDb",
                    "Host=test;Database=test;Username=test;Password=test");
                builder.ConfigureServices(services =>
                {
                    UnauthenticatedTestHostHelpers.ReplaceDbAndRuntime(
                        services,
                        dbName,
                        directoryService,
                        actorProxyFactory,
                        agentProxyResolver);
                });
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetTenantBudget_Unauthenticated_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/tenant/budget", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetTenantBudget_Unauthenticated_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            "/api/v1/tenant/budget",
            new SetBudgetRequest(50.0m),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// Authorisation tests for the agent-scope budget endpoints
/// (<c>GET / PUT /api/v1/tenant/agents/{id}/budget</c>). This was
/// the only group correctly gated pre-#2288; the suite pins the
/// behaviour so the move-auth-into-the-endpoint-file refactor in
/// #2288 doesn't accidentally drop the agent gate too.
/// </summary>
public class AgentBudgetEndpointsUnauthenticatedTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public AgentBudgetEndpointsUnauthenticatedTests()
    {
        var dbName = $"AgentBudgetAuthTestDb_{Guid.NewGuid()}";
        var directoryService = Substitute.For<IDirectoryService>();
        var actorProxyFactory = Substitute.For<IActorProxyFactory>();
        var agentProxyResolver = Substitute.For<IAgentProxyResolver>();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:SpringDb",
                    "Host=test;Database=test;Username=test;Password=test");
                builder.ConfigureServices(services =>
                {
                    UnauthenticatedTestHostHelpers.ReplaceDbAndRuntime(
                        services,
                        dbName,
                        directoryService,
                        actorProxyFactory,
                        agentProxyResolver);
                });
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetAgentBudget_Unauthenticated_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/tenant/agents/{Guid.NewGuid():N}/budget", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetAgentBudget_Unauthenticated_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenant/agents/{Guid.NewGuid():N}/budget",
            new SetBudgetRequest(10.0m),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
