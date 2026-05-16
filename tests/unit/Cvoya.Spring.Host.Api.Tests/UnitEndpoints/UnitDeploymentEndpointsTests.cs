// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.UnitEndpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Client;
using global::Dapr.Workflow;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Handler-level tests for the unit deployment endpoint
/// (<c>GET /api/v1/tenant/units/{id}/deployment</c>) introduced in
/// #2274. The route projects the unit actor's lifecycle status into the
/// portal's deployment-tab shape (<see cref="UnitDeploymentResponse"/>);
/// these tests mock at the actor / directory boundary so the wire-shape
/// contract is exercised without a live Dapr sidecar.
/// </summary>
public class UnitDeploymentEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitDeploymentEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetUnitDeployment_RunningUnit_ReturnsRunningTrue()
    {
        // Arrange: directory resolves the unit and the actor proxy reports Running.
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        var proxy = ArrangeUnit(unitGuid, "engineering", LifecycleStatus.Running);

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{unitGuid:N}/deployment", ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitDeploymentResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Running.ShouldBeTrue();
        body.Status.ShouldBe("Running");
        await proxy.Received(1).GetStatusAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUnitDeployment_StoppedUnit_ReturnsRunningFalseWithStatus()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        ArrangeUnit(unitGuid, "engineering", LifecycleStatus.Stopped);

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{unitGuid:N}/deployment", ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitDeploymentResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Running.ShouldBeFalse();
        body.Status.ShouldBe("Stopped");
    }

    [Fact]
    public async Task GetUnitDeployment_UnknownUnit_Returns404WithProblemDetails()
    {
        // Arrange: directory returns null for any unit address.
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        // Act
        var ghost = Guid.NewGuid();
        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{ghost:N}/deployment", ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        problem.GetProperty("status").GetInt32().ShouldBe(404);
    }

    private IUnitActor ArrangeUnit(Guid unitGuid, string displayName, LifecycleStatus status)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();

        var entry = new DirectoryEntry(
            new Address("unit", unitGuid),
            unitGuid,
            displayName,
            $"unit {displayName}",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == unitGuid),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(status);

        var actorIdStr = unitGuid.ToString("N");
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == actorIdStr),
                Arg.Any<string>())
            .Returns(proxy);
        return proxy;
    }
}

/// <summary>
/// Authorisation tests for the unit deployment endpoint. With no
/// <c>LocalDev</c> setting the host picks <c>ApiTokenScheme</c>, and a
/// missing token must 401 before the route's directory / actor calls
/// run. Mirrors the no-session contract proven by
/// <c>UnitPolicyEndpointsUnauthenticatedTests</c>.
/// </summary>
public class UnitDeploymentEndpointsUnauthenticatedTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public UnitDeploymentEndpointsUnauthenticatedTests()
    {
        var dbName = $"DeploymentAuthTestDb_{Guid.NewGuid()}";
        var directoryService = Substitute.For<IDirectoryService>();
        var actorProxyFactory = Substitute.For<IActorProxyFactory>();
        var agentProxyResolver = Substitute.For<IAgentProxyResolver>();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // No LocalDev setting — the host picks ApiTokenScheme and a
                // missing / invalid token must 401 before the endpoint runs.
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
    public async Task GetDeployment_MissingToken_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/tenant/units/{Guid.NewGuid():N}/deployment", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
