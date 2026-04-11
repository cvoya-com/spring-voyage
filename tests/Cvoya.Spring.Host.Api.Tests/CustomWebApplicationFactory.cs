// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors.Client;
using global::Dapr.Client;
using global::Dapr.Workflow;

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

    /// <summary>
    /// Gets the mock <see cref="IActivityQueryService"/> registered in the test DI container.
    /// </summary>
    public IActivityQueryService ActivityQueryService { get; } = Substitute.For<IActivityQueryService>();

    /// <summary>
    /// Gets the mock <see cref="IActivityObservable"/> registered in the test DI container.
    /// </summary>
    public IActivityObservable ActivityObservable { get; } = Substitute.For<IActivityObservable>();

    /// <summary>
    /// Gets the mock <see cref="IStateStore"/> registered in the test DI container.
    /// </summary>
    public IStateStore StateStore { get; } = Substitute.For<IStateStore>();

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
                typeof(IActorProxyFactory),
                typeof(IStateStore),
                typeof(ICostTracker),
                typeof(IActivityQueryService),
                typeof(IActivityObservable),
                typeof(ActivityBus)
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
            services.AddSingleton(StateStore);
            services.AddSingleton(Substitute.For<ICostTracker>());
            services.AddSingleton(ActivityQueryService);
            services.AddSingleton(ActivityObservable);
            services.AddSingleton(new DirectoryCache());

            // Remove and re-register permission service.
            var permDescriptors = services
                .Where(d => d.ServiceType == typeof(IPermissionService))
                .ToList();
            foreach (var descriptor in permDescriptors)
            {
                services.Remove(descriptor);
            }

            var permissionService = Substitute.For<IPermissionService>();
            services.AddSingleton(permissionService);

            // Dapr runtime dependencies.
            services.AddSingleton(Substitute.For<DaprClient>());
            services.AddDaprWorkflow(options => { });

            services.AddSingleton(sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var permSvc = sp.GetRequiredService<IPermissionService>();
                return new MessageRouter(DirectoryService, ActorProxyFactory, permSvc, loggerFactory);
            });
        });
    }
}