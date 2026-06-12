// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using Cvoya.Spring.Dapr.Data;

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
    /// ADR-0052 / Wave 3 (#2618): the API host delegates persistent-agent
    /// execution to the worker, so the <c>HttpFrontDoor</c> composition
    /// registers <em>none</em> of the persistent-agent execution singletons.
    /// This replaces the Wave 1 "still resolve" assertion — Wave 1 left them
    /// resolvable as acknowledged debt; Wave 3 removes that debt. The
    /// acceptance bar of #2618: <c>AddCvoyaSpringDapr(HttpFrontDoor)</c>
    /// registers zero execution singletons.
    /// </summary>
    [Fact]
    public void ApiHost_DoesNotRegisterExecutionSingletons()
    {
        using var client = _factory.CreateClient();

        _factory.Services.GetService<Cvoya.Spring.Dapr.Execution.PersistentAgentLifecycle>()
            .ShouldBeNull();
        _factory.Services.GetService<Cvoya.Spring.Dapr.Execution.PersistentAgentRegistry>()
            .ShouldBeNull();
        _factory.Services.GetService<Cvoya.Spring.Dapr.Execution.AgentVolumeManager>()
            .ShouldBeNull();
        _factory.Services.GetService<Cvoya.Spring.Dapr.Execution.EphemeralAgentRegistry>()
            .ShouldBeNull();
        _factory.Services.GetService<Cvoya.Spring.Core.Execution.IExecutionDispatcher>()
            .ShouldBeNull();
    }

    /// <summary>
    /// #2627: the API host delegates unit-container teardown to the worker,
    /// so the <c>HttpFrontDoor</c> composition registers none of the
    /// container-lifecycle / unit-teardown / A2A-transport execution services
    /// either. Together with
    /// <see cref="ApiHost_DoesNotRegisterExecutionSingletons"/> this is the
    /// #2627 acceptance bar: <c>AddCvoyaSpringDapr(HttpFrontDoor)</c> registers
    /// ZERO execution services and the API host still boots.
    /// </summary>
    [Fact]
    public void ApiHost_DoesNotRegisterUnitContainerExecutionServices()
    {
        using var client = _factory.CreateClient();

        _factory.Services.GetService<Cvoya.Spring.Core.Execution.IContainerRuntime>()
            .ShouldBeNull();
        _factory.Services.GetService<Cvoya.Spring.Dapr.Execution.ContainerLifecycleManager>()
            .ShouldBeNull();
        _factory.Services.GetService<Cvoya.Spring.Core.Units.IUnitContainerLifecycle>()
            .ShouldBeNull();
        _factory.Services.GetService<Cvoya.Spring.Core.Execution.IDaprSidecarManager>()
            .ShouldBeNull();
        _factory.Services.GetService<Cvoya.Spring.Core.Execution.IA2ATransportFactory>()
            .ShouldBeNull();
        _factory.Services.GetService<Cvoya.Spring.Core.Execution.IAgentContextBuilder>()
            .ShouldBeNull();
    }

    /// <summary>
    /// ADR-0052 / Wave 3 (#2618, #2627): the API host resolves
    /// <see cref="Cvoya.Spring.Dapr.Execution.IExecutionHostGateway"/>
    /// — its delegation channel to the execution host's persistent-agent
    /// surface and unit-container teardown — in place of the execution
    /// singletons.
    /// </summary>
    [Fact]
    public void ApiHost_ResolvesExecutionHostGateway()
    {
        using var client = _factory.CreateClient();

        _factory.Services.GetService<Cvoya.Spring.Dapr.Execution.IExecutionHostGateway>()
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
    /// ADR-0052 / Wave 3 (#2625 + #2618): with the MCP surface served as a
    /// worker Kestrel route and the API persistent-agent endpoints delegating
    /// to the worker, the <c>McpServer</c> / <c>IMcpServer</c> singletons are
    /// execution-host-only — the <c>HttpFrontDoor</c> composition no longer
    /// registers them at all.
    /// </summary>
    [Fact]
    public void ApiHost_DoesNotRegisterMcpServerSingleton()
    {
        using var client = _factory.CreateClient();

        _factory.Services.GetService<Cvoya.Spring.Dapr.Mcp.McpServer>()
            .ShouldBeNull();
        _factory.Services.GetService<Cvoya.Spring.Core.Execution.IMcpServer>()
            .ShouldBeNull();
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}
