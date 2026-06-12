// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.UnitEndpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors.Client;
using global::Dapr.Client;
using global::Dapr.Workflow;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

/// <summary>
/// Shared setup for the per-endpoint "missing token returns 401" suites
/// in this folder. The host is intentionally configured without
/// <c>LocalDev=true</c> so it falls back to <c>ApiTokenScheme</c>; the
/// helper strips the EF Postgres provider, swaps in the in-memory
/// provider, and replaces every Dapr-touching singleton with an
/// NSubstitute double so the host can boot without a sidecar.
/// </summary>
internal static class UnauthenticatedTestHostHelpers
{
    public static void ReplaceDbAndRuntime(
        IServiceCollection services,
        string dbName,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IAgentProxyResolver agentProxyResolver)
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

        services.AddSingleton(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var permSvc = Substitute.For<IPermissionService>();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            return new MessageRouter(directoryService, agentProxyResolver, permSvc, loggerFactory, scopeFactory);
        });
    }
}
