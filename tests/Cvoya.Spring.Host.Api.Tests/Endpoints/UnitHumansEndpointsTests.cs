// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
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
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Regression tests for #976: the unit humans endpoints
/// (<c>PATCH/GET/DELETE /api/v1/units/{id}/humans/...</c>) must honour the
/// <c>UnitOwner</c> / <c>UnitViewer</c> policies correctly in LocalDev,
/// where the caller is the synthesised <c>local-dev-user</c> principal.
/// Before the fix, <see cref="PermissionService"/> addressed the unit
/// actor by the route-level id (the unit name) rather than its Dapr actor
/// id, which caused every request to land on a freshly activated actor
/// with an empty permission map and 403 the caller.
/// </summary>
public class UnitHumansEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitHumansEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task SetHumanPermission_LocalDevCreatorIsOwner_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        // Mirror the post-creation state: the LocalDev caller has Owner
        // on the unit. The permission service is the seam the handler
        // consults — arranging Owner here asserts the gate actually lets
        // the caller through when the service reports Owner (#976).
        ArrangePermission(unitName, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/humans/alice/permissions",
            new SetHumanPermissionRequest("Operator"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetHumanPermissions_LocalDevCreatorIsViewer_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);
        ArrangePermission(unitName, AuthConstants.DefaultLocalUserId, PermissionLevel.Viewer);

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetHumanPermissionsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UnitPermissionEntry>());
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), nameof(UnitActor))
            .Returns(proxy);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{unitName}/humans", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RemoveHumanPermission_LocalDevCreatorIsOwner_Returns204()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);
        ArrangePermission(unitName, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{unitName}/humans/alice/permissions", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SetHumanPermission_CallerHasNoPermission_Returns403()
    {
        // Production path: a caller without an Owner grant must still be
        // denied. The fix only widens "route id vs actor id" resolution —
        // it must not weaken the permission gate itself.
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        // Explicitly arrange "no permission" so we don't depend on the
        // substitute's implicit null default — the assertion documents
        // the expected 403 branch.
        _factory.PermissionService
            .ResolveEffectivePermissionAsync(
                AuthConstants.DefaultLocalUserId, unitName, Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/humans/alice/permissions",
            new SetHumanPermissionRequest("Operator"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SetHumanPermission_CallerHasOnlyViewer_Returns403()
    {
        // The PATCH endpoint is owner-gated; Viewer is insufficient. The
        // fix preserves the gate semantics while correcting the id
        // lookup — asserting both in one test suite.
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);
        ArrangePermission(unitName, AuthConstants.DefaultLocalUserId, PermissionLevel.Viewer);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/humans/alice/permissions",
            new SetHumanPermissionRequest("Operator"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SetHumanPermission_PermissionServiceAddressesUnitByRouteId()
    {
        // The handler passes the route `{id}` — the unit *name* — to the
        // permission service. Verifying this call shape guards against a
        // regression where the auth gate starts consulting a different
        // identifier than the rest of the humans endpoint surface.
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);
        ArrangePermission(unitName, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);

        await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/humans/alice/permissions",
            new SetHumanPermissionRequest("Operator"),
            ct);

        await _factory.PermissionService
            .Received()
            .ResolveEffectivePermissionAsync(
                AuthConstants.DefaultLocalUserId,
                unitName,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetHumanPermission_UnitDoesNotExist_Returns404()
    {
        // #1029: the existence check must run ahead of the permission gate
        // on the /humans sub-routes, the same way it does on /policy. A
        // missing unit is 404 even when the caller has no grant, otherwise
        // the endpoint leaks "unit exists" vs "unit is forbidden" the way
        // the original declarative RequireAuthorization gate did.
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeNotFound(unitName);

        _factory.PermissionService
            .ResolveEffectivePermissionAsync(
                AuthConstants.DefaultLocalUserId, unitName, Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/humans/alice/permissions",
            new SetHumanPermissionRequest("Operator"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetHumanPermissions_UnitDoesNotExist_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeNotFound(unitName);

        _factory.PermissionService
            .ResolveEffectivePermissionAsync(
                AuthConstants.DefaultLocalUserId, unitName, Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{unitName}/humans", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoveHumanPermission_UnitDoesNotExist_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeNotFound(unitName);

        _factory.PermissionService
            .ResolveEffectivePermissionAsync(
                AuthConstants.DefaultLocalUserId, unitName, Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{unitName}/humans/alice/permissions", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static string NewUnitName() => $"humans-test-{Guid.NewGuid():N}";

    private void ArrangeResolved(string unitName)
    {
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == unitName),
                Arg.Any<CancellationToken>())
            .Returns(_ => new DirectoryEntry(
                new Address("unit", unitName),
                $"actor-{Guid.NewGuid():N}",
                unitName,
                "Test unit",
                null,
                DateTimeOffset.UtcNow));
    }

    private void ArrangeNotFound(string unitName)
    {
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == unitName),
                Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);
    }

    private void ArrangePermission(string unitName, string humanId, PermissionLevel level)
    {
        _factory.PermissionService
            .ResolveEffectivePermissionAsync(humanId, unitName, Arg.Any<CancellationToken>())
            .Returns(level);
    }
}

/// <summary>
/// Non-LocalDev coverage for the <c>/humans</c> endpoint group. An
/// unauthenticated caller must receive 401 regardless of what the
/// permission service is arranged to return — the fix for #976 widens
/// how the permission evaluator resolves the unit's actor id, but the
/// authentication layer stays in front of it.
/// </summary>
public class UnitHumansEndpointsUnauthenticatedTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public UnitHumansEndpointsUnauthenticatedTests()
    {
        var dbName = $"HumansAuthTestDb_{Guid.NewGuid()}";
        var directoryService = Substitute.For<IDirectoryService>();
        var actorProxyFactory = Substitute.For<IActorProxyFactory>();
        var agentProxyResolver = Substitute.For<IAgentProxyResolver>();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // No LocalDev setting — the host picks ApiTokenScheme and a
                // missing / invalid token must 401 before the permission
                // handler ever runs.
                builder.UseSetting("ConnectionStrings:SpringDb",
                    "Host=test;Database=test;Username=test;Password=test");
                builder.UseSetting("Secrets:AllowEphemeralDevKey", "true");

                builder.ConfigureServices(services =>
                {
                    var dbDescriptors = services
                        .Where(d => d.ServiceType == typeof(DbContextOptions<SpringDbContext>)
                                 || d.ServiceType == typeof(DbContextOptions)
                                 || d.ServiceType == typeof(SpringDbContext)
                                 || (d.ServiceType.FullName?.StartsWith(
                                        "Microsoft.EntityFrameworkCore.", StringComparison.Ordinal) ?? false)
                                 || (d.ServiceType.FullName?.StartsWith(
                                        "Npgsql.", StringComparison.Ordinal) ?? false))
                        .ToList();
                    foreach (var descriptor in dbDescriptors)
                    {
                        services.Remove(descriptor);
                    }
                    services.AddDbContext<SpringDbContext>(options =>
                        options.UseInMemoryDatabase(dbName));

                    var typesToRemove = new[]
                    {
                        typeof(IDirectoryService),
                        typeof(MessageRouter),
                        typeof(DirectoryCache),
                        typeof(IActorProxyFactory),
                        typeof(IStateStore),
                    };
                    var descriptors = services
                        .Where(d => typesToRemove.Contains(d.ServiceType))
                        .ToList();
                    foreach (var descriptor in descriptors)
                    {
                        services.Remove(descriptor);
                    }

                    services.AddSingleton(directoryService);
                    services.AddSingleton(actorProxyFactory);
                    services.AddSingleton(Substitute.For<IStateStore>());
                    services.AddSingleton(new DirectoryCache());
                    services.AddSingleton(Substitute.For<DaprClient>());
                    services.AddDaprWorkflow(options => { });

                    var workflowWorkerDescriptors = services
                        .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                            && d.ImplementationType?.FullName?.Contains(
                                "Dapr.Workflow", StringComparison.Ordinal) == true)
                        .ToList();
                    foreach (var d in workflowWorkerDescriptors)
                    {
                        services.Remove(d);
                    }

                    var costDescriptors = services
                        .Where(d => d.ServiceType == typeof(ICostTracker))
                        .ToList();
                    foreach (var d in costDescriptors)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton(Substitute.For<ICostTracker>());

                    services.AddSingleton(sp =>
                    {
                        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                        var permSvc = Substitute.For<IPermissionService>();
                        return new MessageRouter(directoryService, agentProxyResolver, permSvc, loggerFactory);
                    });
                });
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SetHumanPermission_MissingToken_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        // No Authorization header set.

        var response = await client.PatchAsJsonAsync(
            "/api/v1/tenant/units/any-unit/humans/alice/permissions",
            new SetHumanPermissionRequest("Operator"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetHumanPermissions_MissingToken_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/tenant/units/any-unit/humans", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}