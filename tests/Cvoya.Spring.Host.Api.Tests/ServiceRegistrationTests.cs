// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
                // #639 SecretsConfigurationRequirement — use an ephemeral
                // dev key so the validator reports Met+Warning instead of
                // aborting on missing key material.
                builder.UseSetting("Secrets:AllowEphemeralDevKey", "true");

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

    public void Dispose()
    {
        _factory.Dispose();
    }
}