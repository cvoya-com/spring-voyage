// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Shouldly;

using Xunit;

/// <summary>
/// Validates that all endpoint parameter types are properly registered in the DI container.
/// This test catches missing service registrations that <see cref="CustomWebApplicationFactory"/>
/// masks by replacing services with mocks.
/// </summary>
public class ServiceRegistrationTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public ServiceRegistrationTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("LocalDev", "true");
                // Satisfy the #261 fail-fast ConnectionStrings:SpringDb check.
                // AddCvoyaSpringDapr runs before ConfigureServices below
                // replaces the DbContext with an in-memory provider.
                builder.UseSetting("ConnectionStrings:SpringDb",
                    "Host=test;Database=test;Username=test;Password=test");
                // #2518: Dispatcher:BaseUrl is mandatory on the API host —
                // PersistentAgentRegistry is registered as a hosted service.
                // Set a syntactically valid URL so the validator passes.
                // The string is never actually called: this test only
                // exercises endpoint DI resolution.
                builder.UseSetting("Dispatcher:BaseUrl", "http://spring-dispatcher.test/");
                builder.UseSetting("Dispatcher:BearerToken", "test-token");
                // #2597: CallbackBaseUrl:BaseUrl is mandatory on the
                // API host — the agent-runtime launcher stamps it onto
                // runtime containers as SPRING_CALLBACK_URL. Set a
                // syntactically valid URL so the validator passes.
                builder.UseSetting("CallbackBaseUrl:BaseUrl", "http://spring-caddy.test:8443/");
                // #639 SecretsConfigurationRequirement — use an ephemeral
                // dev key so the validator reports Met+Warning instead of
                // aborting on missing key material.
                builder.ConfigureServices(services =>
                {
                    // This test validates endpoint DI resolution only. Keep the
                    // DaprWorkflowClient registrations but strip the workflow
                    // worker so factory disposal does not trip the known Dapr
                    // Workflow shutdown bug tracked in #568.
                    services.RemoveDaprWorkflowWorker();

                    // Replace only the DB with in-memory — keep all other DI registrations intact.
                    // Also strip EF / Npgsql internal-service registrations so the swap does
                    // not trip EF's "multiple providers registered" guard.
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
                        options.UseInMemoryDatabase($"DiValidation_{Guid.NewGuid()}"));
                });
            });
    }

    [Fact]
    public void AppStartup_AllEndpointParametersResolvable()
    {
        // Creating the server triggers endpoint resolution, which fails if any
        // minimal API handler parameter cannot be resolved from the DI container.
        using var client = _factory.CreateClient();
    }

    /// <summary>
    /// ADR-0052: the API host composes with
    /// <see cref="SpringHostRole.HttpFrontDoor"/>, so the four worker-only
    /// execution hosted services
    /// (<see cref="Cvoya.Spring.Dapr.Execution.AgentVolumeManager"/>,
    /// <see cref="Cvoya.Spring.Dapr.Execution.PersistentAgentRegistry"/>,
    /// <see cref="Cvoya.Spring.Dapr.Execution.EphemeralAgentRegistry"/>,
    /// <see cref="Cvoya.Spring.Dapr.Execution.ContainerHealthMetricsService"/>)
    /// must NOT register as <see cref="IHostedService"/> on the API host.
    /// </summary>
    [Fact]
    public void ApiHost_DoesNotRegisterExecutionHostedServices()
    {
        // Triggers host build so the DI container is populated.
        using var client = _factory.CreateClient();

        var hosted = _factory.Services.GetServices<IHostedService>().ToList();

        hosted.ShouldNotContain(s => s is Cvoya.Spring.Dapr.Execution.AgentVolumeManager);
        hosted.ShouldNotContain(s => s is Cvoya.Spring.Dapr.Execution.PersistentAgentRegistry);
        hosted.ShouldNotContain(s => s is Cvoya.Spring.Dapr.Execution.EphemeralAgentRegistry);
        hosted.ShouldNotContain(s => s is Cvoya.Spring.Dapr.Execution.ContainerHealthMetricsService);
    }

    /// <summary>
    /// ADR-0052: gating only the <c>AddHostedService</c> wrappers must leave
    /// the execution DI singletons resolvable on the API host — endpoint code
    /// resolves <see cref="Cvoya.Spring.Dapr.Execution.PersistentAgentRegistry"/>
    /// as a plain singleton.
    /// </summary>
    [Fact]
    public void ApiHost_ExecutionSingletonsStillResolve()
    {
        using var client = _factory.CreateClient();

        _factory.Services.GetService<Cvoya.Spring.Dapr.Execution.PersistentAgentRegistry>()
            .ShouldNotBeNull();
        _factory.Services.GetService<Cvoya.Spring.Dapr.Execution.AgentVolumeManager>()
            .ShouldNotBeNull();
        _factory.Services.GetService<Cvoya.Spring.Dapr.Execution.EphemeralAgentRegistry>()
            .ShouldNotBeNull();
    }

    /// <summary>
    /// ADR-0052 §2 / PR 2 of #2611 (#2614): the <c>McpServer</c> hosted
    /// service runs worker-only — there is one session authority, co-located
    /// with the dispatcher. The API host (a stateless HTTP front door) must
    /// NOT register it as an <see cref="IHostedService"/>.
    /// </summary>
    [Fact]
    public void ApiHost_DoesNotRegisterMcpServerHostedService()
    {
        using var client = _factory.CreateClient();

        var hosted = _factory.Services.GetServices<IHostedService>().ToList();

        hosted.ShouldNotContain(
            s => s is Cvoya.Spring.Dapr.Mcp.McpServer,
            "McpServer must NOT register as an IHostedService on the API host; " +
            "ADR-0052 §2 places the single session authority worker-only.");
    }

    /// <summary>
    /// ADR-0052 §1: the <c>McpServer</c> / <c>IMcpServer</c> DI singletons
    /// stay resolvable on the API host even though the hosted service is
    /// worker-only — OpenAPI doc-gen and the latent dispatcher singleton
    /// still resolve them.
    /// </summary>
    [Fact]
    public void ApiHost_McpServerSingletonStillResolves()
    {
        using var client = _factory.CreateClient();

        _factory.Services.GetService<Cvoya.Spring.Dapr.Mcp.McpServer>()
            .ShouldNotBeNull();
        _factory.Services.GetService<Cvoya.Spring.Core.Execution.IMcpServer>()
            .ShouldNotBeNull();
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}
