// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Net;
using System.Net.Http.Headers;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Auth;
using global::Dapr.Actors.Client;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
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

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Do NOT set LocalDev so ApiTokenScheme is used.
                builder.ConfigureServices(services =>
                {
                    // Replace DbContext with in-memory.
                    var dbDescriptors = services
                        .Where(d => d.ServiceType == typeof(DbContextOptions<SpringDbContext>)
                                 || d.ServiceType == typeof(DbContextOptions)
                                 || d.ServiceType == typeof(SpringDbContext))
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

                    services.AddSingleton(sp =>
                    {
                        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                        return new MessageRouter(directoryService, actorProxyFactory, loggerFactory);
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

        var response = await client.GetAsync("/api/v1/auth/tokens", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
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

        var response = await client.GetAsync("/api/v1/auth/tokens", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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

        var response = await client.GetAsync("/api/v1/auth/tokens", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MissingToken_ReturnsUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;

        var client = _factory.CreateClient();
        // No Authorization header set.

        var response = await client.GetAsync("/api/v1/auth/tokens", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InvalidToken_ReturnsUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "completely-bogus-token");

        var response = await client.GetAsync("/api/v1/auth/tokens", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
            TenantId = Guid.Empty,
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
