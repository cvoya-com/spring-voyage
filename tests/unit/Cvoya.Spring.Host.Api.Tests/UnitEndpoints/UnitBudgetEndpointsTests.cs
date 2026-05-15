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
/// Handler-level tests for the unit-keyed budget endpoints
/// (<c>GET / PUT /api/v1/tenant/units/{id}/budget</c>) introduced in
/// #2280.
/// </summary>
/// <remarks>
/// <para>
/// The happy-path, 404, validation, and tenant-isolation cases for
/// these routes already live alongside the agent/tenant budget tests
/// in <see cref="BudgetEndpointsTests"/> (search for
/// <c>SetUnitBudget_*</c> / <c>GetUnitBudget_*</c>). The existing
/// coverage is intentionally not duplicated under this folder.
/// </para>
/// <para>
/// This file pins the authentication contract — the auth precedent for
/// these routes documented here, alongside the rest of the
/// <c>UnitEndpoints/</c> suites added in #2285.
/// </para>
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
                // 401 before its handler runs. The unit-budget routes
                // happen to currently *bypass* that gate (see test note
                // below), so they take the same path the existing
                // SetUnitBudget_ZeroBudget_ReturnsBadRequest pins:
                // through to the in-memory EF handler.
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

    // The unit-budget routes are mapped inside MapBudgetEndpoints on a
    // separate `unitGroup` from the `agentGroup` that the
    // .RequireAuthorization(RolePolicies.TenantOperator) wiring in
    // Program.cs targets — MapBudgetEndpoints returns only the agent
    // group, so the auth chain in Program.cs binds to agents and never
    // reaches the unit-scoped group. These tests pin the current
    // unauthenticated behaviour rather than a hypothetical 401, so the
    // gap (if intentional, ratify; if accidental, fix) shows up as a
    // visible expectation rather than a silent "works on my machine".

    [Fact]
    public async Task GetBudget_NoAuthGate_ReachesHandlerAndReturns404ForMissingBudget()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Act — no Authorization header, no LocalDev shortcut.
        var response = await client.GetAsync(
            $"/api/v1/tenant/units/{Guid.NewGuid():N}/budget", ct);

        // Assert — the handler runs and returns 404 (no row planted),
        // confirming routing did NOT short-circuit at auth. If a future
        // PR wraps the unit-budget group in .RequireAuthorization() this
        // test will turn into a 401, surfacing the regression target.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SetBudget_NoAuthGate_ReachesHandlerAndReturns200OnValidBody()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Act — valid body so the EF upsert runs cleanly.
        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{Guid.NewGuid():N}/budget",
            new SetBudgetRequest(25.50m),
            ct);

        // Assert — the handler runs and upserts. As above, the moment
        // the unit-budget group gets .RequireAuthorization() this will
        // flip to 401.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
