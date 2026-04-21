// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

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
/// Authorization tests for the unit-policy endpoints
/// (<c>GET /api/v1/units/{id}/policy</c> and
/// <c>PUT /api/v1/units/{id}/policy</c>) introduced by #1001. The endpoints
/// previously ran with no unit-scoped permission gate, so any authenticated
/// caller could read or overwrite any unit's governance policy. This suite
/// pins the <c>UnitViewer</c> / <c>UnitOwner</c> gates symmetrically with
/// the <c>/humans</c> sub-routes (see <see cref="UnitHumansEndpointsTests"/>).
/// The fix depends on the #996 <see cref="PermissionService"/> actor-id
/// lookup: without it, the LocalDev caller would 403 on their own unit.
/// </summary>
public class UnitPolicyEndpointsAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitPolicyEndpointsAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task SetPolicy_LocalDevCreatorIsOwner_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);
        ArrangePermission(unitName, AuthConstants.DefaultLocalUserId, PermissionLevel.Owner);

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/units/{unitName}/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "delete_repo" })),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPolicy_LocalDevCreatorIsViewer_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);
        ArrangePermission(unitName, AuthConstants.DefaultLocalUserId, PermissionLevel.Viewer);

        var response = await _client.GetAsync(
            $"/api/v1/units/{unitName}/policy", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SetPolicy_CallerHasNoPermission_Returns403()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        // Explicit null arrangement documents the "no grant" branch; the
        // Owner gate must still refuse a non-owner, otherwise a second
        // tenant's caller could overwrite the first tenant's policy.
        _factory.PermissionService
            .ResolveEffectivePermissionAsync(
                AuthConstants.DefaultLocalUserId, unitName, Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/units/{unitName}/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "delete_repo" })),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SetPolicy_CallerHasOnlyViewer_Returns403()
    {
        // PUT is owner-gated; Viewer is insufficient. Mirrors the humans
        // endpoint's PATCH/Viewer guard so the auth shape is symmetric
        // across unit-scoped administrative verbs.
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);
        ArrangePermission(unitName, AuthConstants.DefaultLocalUserId, PermissionLevel.Viewer);

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/units/{unitName}/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "delete_repo" })),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetPolicy_CallerHasNoPermission_Returns403()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        _factory.PermissionService
            .ResolveEffectivePermissionAsync(
                AuthConstants.DefaultLocalUserId, unitName, Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var response = await _client.GetAsync(
            $"/api/v1/units/{unitName}/policy", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static string NewUnitName() => $"policy-auth-{Guid.NewGuid():N}";

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

    private void ArrangePermission(string unitName, string humanId, PermissionLevel level)
    {
        _factory.PermissionService
            .ResolveEffectivePermissionAsync(humanId, unitName, Arg.Any<CancellationToken>())
            .Returns(level);
    }
}

/// <summary>
/// Non-LocalDev coverage for the unit-policy endpoint group. An
/// unauthenticated caller must receive 401 on both verbs regardless of
/// what the permission service is arranged to return; the new
/// <c>UnitOwner</c> / <c>UnitViewer</c> policies run *after* the
/// authentication layer, so missing credentials still short-circuit.
/// </summary>
public class UnitPolicyEndpointsUnauthenticatedTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public UnitPolicyEndpointsUnauthenticatedTests()
    {
        var dbName = $"PolicyAuthTestDb_{Guid.NewGuid()}";
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
    public async Task GetPolicy_MissingToken_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/units/any-unit/policy", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetPolicy_MissingToken_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            "/api/v1/units/any-unit/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "x" })),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}