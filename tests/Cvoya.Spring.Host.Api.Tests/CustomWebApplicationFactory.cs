/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Host.Api.Tests;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Routing;
using global::Dapr.Actors.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that replaces Dapr-dependent
/// services with test doubles, allowing integration tests to run without a Dapr sidecar.
/// Uses local dev mode to bypass authentication in tests.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Gets the mock <see cref="IDirectoryService"/> registered in the test DI container.
    /// </summary>
    public IDirectoryService DirectoryService { get; } = Substitute.For<IDirectoryService>();

    /// <summary>
    /// Gets the mock <see cref="IActorProxyFactory"/> registered in the test DI container.
    /// </summary>
    public IActorProxyFactory ActorProxyFactory { get; } = Substitute.For<IActorProxyFactory>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Use --local mode to enable LocalDevAuthHandler (bypasses auth).
        builder.UseSetting("LocalDev", "true");

        builder.ConfigureServices(services =>
        {
            // Replace the real SpringDbContext with an in-memory database.
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<SpringDbContext>)
                         || d.ServiceType == typeof(DbContextOptions)
                         || d.ServiceType == typeof(SpringDbContext))
                .ToList();

            foreach (var descriptor in dbDescriptors)
            {
                services.Remove(descriptor);
            }

            var dbName = $"TestDb_{Guid.NewGuid()}";
            services.AddDbContext<SpringDbContext>(options =>
                options.UseInMemoryDatabase(dbName));

            // Remove existing registrations that depend on Dapr runtime.
            var typesToRemove = new[]
            {
                typeof(IDirectoryService),
                typeof(MessageRouter),
                typeof(DirectoryCache),
                typeof(IActorProxyFactory)
            };

            var descriptors = services
                .Where(d => typesToRemove.Contains(d.ServiceType))
                .ToList();

            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            // Re-register with test doubles.
            services.AddSingleton(DirectoryService);
            services.AddSingleton(ActorProxyFactory);
            services.AddSingleton(new DirectoryCache());

            services.AddSingleton(sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                return new MessageRouter(DirectoryService, ActorProxyFactory, loggerFactory);
            });
        });
    }
}
