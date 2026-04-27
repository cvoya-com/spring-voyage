// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Net;
using System.Net.Http.Headers;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Auth;

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
/// Tests for <see cref="ApiTokenAuthHandler"/> verifying token validation behavior.
/// These tests use the ApiToken auth scheme (non-local-dev mode) to exercise real token validation.
/// </summary>
public class ApiTokenAuthHandlerTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbName = $"AuthTestDb_{Guid.NewGuid()}";

    public ApiTokenAuthHandlerTests()
    {
        var directoryService = Substitute.For<IDirectoryService>();
        var actorProxyFactory = Substitute.For<IActorProxyFactory>();
        var agentProxyResolver = Substitute.For<IAgentProxyResolver>();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Do NOT set LocalDev so ApiTokenScheme is used.
                // Satisfy the #261 fail-fast connection-string check —
                // AddCvoyaSpringDapr runs in Program.cs before the
                // ConfigureServices callback replaces the DbContext with
                // an in-memory provider. The value is never opened.
                builder.UseSetting("ConnectionStrings:SpringDb",
                    "Host=test;Database=test;Username=test;Password=test");
                // #639 SecretsConfigurationRequirement — use an ephemeral
                // dev key so the validator reports Met+Warning instead of
                // aborting on missing key material.
                builder.UseSetting("Secrets:AllowEphemeralDevKey", "true");

                builder.ConfigureServices(services =>
                {
                    // Replace DbContext with in-memory. Strip EF / Npgsql
                    // internal-service registrations too so the swap does
                    // not hit "multiple providers registered".
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
                        options.UseInMemoryDatabase(_dbName));

                    // Remove Dapr-dependent services and replace with test doubles.
                    var typesToRemove = new[]
                    {
                        typeof(IDirectoryService),
                        typeof(MessageRouter),
                        typeof(DirectoryCache),
                        typeof(IActorProxyFactory),
                        typeof(IStateStore)
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

                    // Strip the Dapr WorkflowWorker IHostedService to avoid the
                    // ObjectDisposedException race on host teardown (#568). The
                    // tests don't drive workflow execution, so the worker's
                    // background gRPC stream is dead weight.
                    var workflowWorkerDescriptors = services
                        .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                            && d.ImplementationType?.FullName?.Contains(
                                "Dapr.Workflow", StringComparison.Ordinal) == true)
                        .ToList();
                    foreach (var d in workflowWorkerDescriptors)
                    {
                        services.Remove(d);
                    }

                    // Remove and re-register ICostTracker.
                    var costDescriptors = services
                        .Where(d => d.ServiceType == typeof(Cvoya.Spring.Core.Costs.ICostTracker))
                        .ToList();
                    foreach (var d in costDescriptors)
                    {
                        services.Remove(d);
                    }

                    services.AddSingleton(Substitute.For<Cvoya.Spring.Core.Costs.ICostTracker>());

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
    }

    [Fact]
    public async Task ValidToken_AuthenticatesSuccessfully()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed a valid token in the database.
        var rawToken = "test-valid-token-abc123";
        var tokenHash = ApiTokenAuthHandler.HashToken(rawToken);
        await SeedTokenAsync(tokenHash, "valid-token");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.GetAsync("/api/v1/tenant/auth/tokens", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RevokedToken_ReturnsUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;

        var rawToken = "test-revoked-token-abc123";
        var tokenHash = ApiTokenAuthHandler.HashToken(rawToken);
        await SeedTokenAsync(tokenHash, "revoked-token", revokedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.GetAsync("/api/v1/tenant/auth/tokens", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredToken_ReturnsUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;

        var rawToken = "test-expired-token-abc123";
        var tokenHash = ApiTokenAuthHandler.HashToken(rawToken);
        await SeedTokenAsync(tokenHash, "expired-token", expiresAt: DateTimeOffset.UtcNow.AddHours(-1));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.GetAsync("/api/v1/tenant/auth/tokens", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MissingToken_ReturnsUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;

        var client = _factory.CreateClient();
        // No Authorization header set.

        var response = await client.GetAsync("/api/v1/tenant/auth/tokens", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InvalidToken_ReturnsUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "completely-bogus-token");

        var response = await client.GetAsync("/api/v1/tenant/auth/tokens", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task SeedTokenAsync(
        string tokenHash,
        string name,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        dbContext.ApiTokens.Add(new ApiTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = "test-user",
            TokenHash = tokenHash,
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt
        });

        await dbContext.SaveChangesAsync();
    }
}