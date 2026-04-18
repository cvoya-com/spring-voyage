// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Units;
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
    /// Gets the mock <see cref="IAgentProxyResolver"/> registered in the test DI container.
    /// </summary>
    public IAgentProxyResolver AgentProxyResolver { get; } = Substitute.For<IAgentProxyResolver>();

    /// <summary>
    /// Gets the mock <see cref="IActivityQueryService"/> registered in the test DI container.
    /// </summary>
    public IActivityQueryService ActivityQueryService { get; } = Substitute.For<IActivityQueryService>();

    /// <summary>
    /// Gets the mock <see cref="IAnalyticsQueryService"/> registered in the test DI container.
    /// Tests that exercise <c>/api/v1/analytics/*</c> arrange responses on this mock.
    /// </summary>
    public IAnalyticsQueryService AnalyticsQueryService { get; } = Substitute.For<IAnalyticsQueryService>();

    /// <summary>
    /// Gets the mock <see cref="IActivityEventBus"/> registered in the test DI container.
    /// </summary>
    public IActivityEventBus ActivityEventBus { get; } = Substitute.For<IActivityEventBus>();

    /// <summary>
    /// Gets the mock <see cref="IUnitActivityObservable"/> registered in the test DI container.
    /// </summary>
    public IUnitActivityObservable UnitActivityObservable { get; } = Substitute.For<IUnitActivityObservable>();

    /// <summary>
    /// Gets the mock <see cref="IStateStore"/> registered in the test DI container.
    /// </summary>
    public IStateStore StateStore { get; } = Substitute.For<IStateStore>();

    /// <summary>
    /// Gets the mock <see cref="IUnitContainerLifecycle"/> registered in the test DI container.
    /// </summary>
    public IUnitContainerLifecycle UnitContainerLifecycle { get; } = Substitute.For<IUnitContainerLifecycle>();

    /// <summary>
    /// Gets the mock <see cref="IGitHubWebhookRegistrar"/> registered in the test DI container.
    /// </summary>
    public IGitHubWebhookRegistrar GitHubWebhookRegistrar { get; } = Substitute.For<IGitHubWebhookRegistrar>();

    /// <summary>
    /// Gets the mock <see cref="IUnitConnectorConfigStore"/> registered in
    /// the test DI container. Tests that exercise connector bindings arrange
    /// responses on this mock to control what the generic
    /// <c>/api/v1/units/{id}/connector</c> endpoints see.
    /// </summary>
    public IUnitConnectorConfigStore ConnectorConfigStore { get; } = Substitute.For<IUnitConnectorConfigStore>();

    /// <summary>
    /// Gets the mock <see cref="IUnitConnectorRuntimeStore"/> registered in
    /// the test DI container.
    /// </summary>
    public IUnitConnectorRuntimeStore ConnectorRuntimeStore { get; } = Substitute.For<IUnitConnectorRuntimeStore>();

    /// <summary>
    /// Stub <see cref="IConnectorType"/> with a known type id used to drive
    /// connector-dispatch tests without depending on the real GitHub
    /// implementation. The stub is registered as the single
    /// <see cref="IConnectorType"/> service in the test DI container.
    /// </summary>
    public IConnectorType StubConnectorType { get; } = CreateStubConnector();

    /// <summary>
    /// Gets the substitute <see cref="ISecretStore"/> wired into the test
    /// DI container. Tests that need to observe (or control) store-layer
    /// writes and deletes configure this stub instead of using a real
    /// Dapr-backed store. <c>WriteAsync</c> is pre-configured to return a
    /// fresh opaque GUID on each call so pass-through writes produce a
    /// valid, unique, opaque store key.
    /// </summary>
    public ISecretStore SecretStore { get; } = CreateStubSecretStore();

    /// <summary>
    /// Gets the substitute <see cref="ISecretAccessPolicy"/> wired into
    /// the test DI container. Defaults to allow-all; tests that exercise
    /// the 403 path re-configure it per call.
    /// </summary>
    public ISecretAccessPolicy SecretAccessPolicy { get; } = CreatePermissiveAccessPolicy();

    /// <summary>
    /// Gets the substitute <see cref="IExpertiseSearch"/> wired into the
    /// test DI container (#542). Tests that exercise
    /// <c>POST /api/v1/directory/search</c> arrange responses on this mock.
    /// </summary>
    public IExpertiseSearch ExpertiseSearch { get; } = Substitute.For<IExpertiseSearch>();

    private static ISecretStore CreateStubSecretStore()
    {
        var stub = Substitute.For<ISecretStore>();
        // Return a fresh opaque GUID on each WriteAsync so pass-through
        // flows produce unique, opaque store keys — mirroring the real
        // Dapr-backed store's contract.
        stub.WriteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Guid.NewGuid().ToString("N")));
        stub.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        stub.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return stub;
    }

    private static ISecretAccessPolicy CreatePermissiveAccessPolicy()
    {
        var stub = Substitute.For<ISecretAccessPolicy>();
        stub.IsAuthorizedAsync(
                Arg.Any<SecretAccessAction>(),
                Arg.Any<SecretScope>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        return stub;
    }

    private static IConnectorType CreateStubConnector()
    {
        var stub = Substitute.For<IConnectorType>();
        stub.TypeId.Returns(new Guid("00000000-0000-0000-0000-00000000beef"));
        stub.Slug.Returns("stub");
        stub.DisplayName.Returns("Stub");
        stub.Description.Returns("Test-only connector stub");
        stub.ConfigType.Returns(typeof(object));
        stub.GetConfigSchemaAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<System.Text.Json.JsonElement?>(null));
        stub.OnUnitStartingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        stub.OnUnitStoppingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return stub;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Use --local mode to enable LocalDevAuthHandler (bypasses auth).
        builder.UseSetting("LocalDev", "true");

        // Satisfy the AddCvoyaSpringDapr fail-fast connection-string check
        // (#261). AddCvoyaSpringDapr runs inside Program.cs BEFORE
        // ConfigureServices below replaces the DbContext with an in-memory
        // provider, so a missing ConnectionStrings:SpringDb would throw.
        // The value is never opened — the in-memory registration below
        // supersedes it — it just has to be a non-empty string.
        builder.UseSetting("ConnectionStrings:SpringDb",
            "Host=test;Database=test;Username=test;Password=test");

        builder.ConfigureServices(services =>
        {
            // Replace the real SpringDbContext with an in-memory database.
            // With #261's fail-fast Npgsql registration we also have to
            // strip the Npgsql-provider descriptors EF Core injected via
            // UseNpgsql — leaving them in place alongside UseInMemoryDatabase
            // triggers EF's "multiple providers registered" guard.
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
                typeof(IAgentProxyResolver),
                typeof(IStateStore),
                typeof(ICostTracker),
                typeof(IActivityQueryService),
                typeof(IAnalyticsQueryService),
                typeof(IActivityEventBus),
                typeof(IUnitActivityObservable),
                typeof(IUnitContainerLifecycle),
                typeof(IGitHubWebhookRegistrar),
                typeof(IUnitConnectorConfigStore),
                typeof(IUnitConnectorRuntimeStore),
                typeof(IConnectorType),
                typeof(ISecretStore),
                typeof(ISecretAccessPolicy),
                typeof(IExpertiseSearch),
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
            services.AddSingleton(AgentProxyResolver);
            services.AddSingleton(StateStore);
            services.AddSingleton(Substitute.For<ICostTracker>());
            services.AddSingleton(ActivityQueryService);
            services.AddSingleton(AnalyticsQueryService);
            services.AddSingleton(ActivityEventBus);
            services.AddSingleton(UnitActivityObservable);
            services.AddSingleton(UnitContainerLifecycle);
            services.AddSingleton(GitHubWebhookRegistrar);
            services.AddSingleton(ConnectorConfigStore);
            services.AddSingleton(ConnectorRuntimeStore);
            services.AddSingleton(StubConnectorType);
            services.AddSingleton(SecretStore);
            services.AddSingleton(SecretAccessPolicy);
            services.AddSingleton(ExpertiseSearch);
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
                return new MessageRouter(DirectoryService, AgentProxyResolver, permSvc, loggerFactory);
            });
        });
    }
}