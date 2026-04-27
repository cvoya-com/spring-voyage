// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Auth;

using global::Dapr.Actors.Client;
using global::Dapr.Client;
using global::Dapr.Workflow;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end tests verifying both OSS auth handlers
/// (<see cref="LocalDevAuthHandler"/>, <see cref="ApiTokenAuthHandler"/>)
/// emit all three platform-role claims for every authenticated caller via
/// the registered <see cref="IRoleClaimSource"/>. Exercises C1.2a / #1257
/// acceptance criterion (a): "every authenticated caller has all three
/// claims under OSS".
/// </summary>
/// <remarks>
/// The tests run the auth handler against a constructed <see cref="HttpContext"/>
/// so the assertion targets the principal the handler actually produces —
/// not a downstream endpoint response — which is the cleanest way to verify
/// claim emission. The full request/response path is covered separately by
/// the policy tests (<see cref="RolePoliciesTests"/>) and the existing
/// <c>ApiTokenAuthHandlerTests</c>.
/// </remarks>
public class AuthHandlerRoleClaimsTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _localDevFactory;
    private readonly WebApplicationFactory<Program> _apiTokenFactory;
    private readonly string _dbName = $"AuthRoleClaimsTestDb_{Guid.NewGuid()}";

    public AuthHandlerRoleClaimsTests()
    {
        _localDevFactory = BuildFactory(localDev: true);
        _apiTokenFactory = BuildFactory(localDev: false);
    }

    public void Dispose()
    {
        _localDevFactory.Dispose();
        _apiTokenFactory.Dispose();
    }

    [Fact]
    public async Task LocalDevAuthHandler_AnyRequest_EmitsAllThreeRoleClaims()
    {
        // Spin up the host so DI/auth registration mirrors production wiring.
        _ = _localDevFactory.CreateClient();

        var roles = await AuthenticateAndExtractRolesAsync(
            _localDevFactory, AuthConstants.LocalDevScheme, includeBearerToken: false);

        roles.ShouldBe(
            new[]
            {
                PlatformRoles.PlatformOperator,
                PlatformRoles.TenantOperator,
                PlatformRoles.TenantUser,
            },
            ignoreOrder: true);
    }

    [Fact]
    public async Task ApiTokenAuthHandler_ValidToken_EmitsAllThreeRoleClaims()
    {
        _ = _apiTokenFactory.CreateClient();

        var rawToken = "test-role-claims-token";
        var tokenHash = ApiTokenAuthHandler.HashToken(rawToken);
        await SeedTokenAsync(_apiTokenFactory, tokenHash, "role-claims-token");

        var roles = await AuthenticateAndExtractRolesAsync(
            _apiTokenFactory, AuthConstants.ApiTokenScheme, includeBearerToken: true, rawToken);

        roles.ShouldBe(
            new[]
            {
                PlatformRoles.PlatformOperator,
                PlatformRoles.TenantOperator,
                PlatformRoles.TenantUser,
            },
            ignoreOrder: true);
    }

    [Fact]
    public async Task LocalDevAuthHandler_PrincipalIsAuthenticated()
    {
        // Sanity check that the role-claim wiring did not regress the
        // baseline contract — the principal is still authenticated, the
        // NameIdentifier is still the default local user id.
        _ = _localDevFactory.CreateClient();

        var (succeeded, principal) = await AuthenticateAsync(
            _localDevFactory, AuthConstants.LocalDevScheme, includeBearerToken: false);

        succeeded.ShouldBeTrue();
        principal!.Identity!.IsAuthenticated.ShouldBeTrue();
        principal.FindFirstValue(ClaimTypes.NameIdentifier).ShouldBe(AuthConstants.DefaultLocalUserId);
    }

    private static async Task<IReadOnlyList<string>> AuthenticateAndExtractRolesAsync(
        WebApplicationFactory<Program> factory,
        string scheme,
        bool includeBearerToken,
        string? rawToken = null)
    {
        var (succeeded, principal) = await AuthenticateAsync(factory, scheme, includeBearerToken, rawToken);
        succeeded.ShouldBeTrue();

        return principal!
            .FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToList();
    }

    private static async Task<(bool Succeeded, ClaimsPrincipal? Principal)> AuthenticateAsync(
        WebApplicationFactory<Program> factory,
        string scheme,
        bool includeBearerToken,
        string? rawToken = null)
    {
        using var scope = factory.Services.CreateScope();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };

        if (includeBearerToken && rawToken is not null)
        {
            httpContext.Request.Headers.Authorization = $"Bearer {rawToken}";
        }

        var authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var result = await authService.AuthenticateAsync(httpContext, scheme);
        return (result.Succeeded, result.Principal);
    }

    private static async Task SeedTokenAsync(
        WebApplicationFactory<Program> factory,
        string tokenHash,
        string name)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        dbContext.ApiTokens.Add(new ApiTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = "test-user",
            TokenHash = tokenHash,
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private WebApplicationFactory<Program> BuildFactory(bool localDev)
    {
        var directoryService = Substitute.For<IDirectoryService>();
        var actorProxyFactory = Substitute.For<IActorProxyFactory>();
        var agentProxyResolver = Substitute.For<IAgentProxyResolver>();
        var dbName = $"{_dbName}_{(localDev ? "local" : "token")}";

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                if (localDev)
                {
                    builder.UseSetting("LocalDev", "true");
                }

                // Satisfy the #261 fail-fast connection-string check —
                // AddCvoyaSpringDapr runs in Program.cs before ConfigureServices
                // replaces the DbContext with an in-memory provider.
                builder.UseSetting("ConnectionStrings:SpringDb",
                    "Host=test;Database=test;Username=test;Password=test");
                builder.UseSetting("Secrets:AllowEphemeralDevKey", "true");

                builder.ConfigureServices(services =>
                {
                    // #568: strip the Dapr workflow worker so factory
                    // disposal doesn't trip the ObjectDisposedException race.
                    services.RemoveDaprWorkflowWorker();

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
                        var permSvc = Substitute.For<IPermissionService>();
                        var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
                        return new MessageRouter(directoryService, agentProxyResolver, permSvc, loggerFactory);
                    });
                });
            });
    }
}