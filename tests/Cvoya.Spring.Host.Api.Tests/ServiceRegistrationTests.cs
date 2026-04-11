// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using Cvoya.Spring.Dapr.Data;

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

                builder.ConfigureServices(services =>
                {
                    // Replace only the DB with in-memory — keep all other DI registrations intact.
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
