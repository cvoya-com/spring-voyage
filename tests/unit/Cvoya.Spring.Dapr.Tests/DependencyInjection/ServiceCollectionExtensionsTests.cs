// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.DependencyInjection;

using System.Collections.Generic;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Messaging;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors.Client;
using global::Dapr.Workflow;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies that <see cref="ServiceCollectionExtensions"/> registers all expected services.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(Substitute.For<IActorProxyFactory>());
        // Pre-register an in-memory SpringDbContext so AddCvoyaSpringDapr
        // respects the test-harness override and skips its mandatory
        // ConnectionStrings:SpringDb check (see #261).
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase($"DiTest_{Guid.NewGuid():N}"));
        services.AddCvoyaSpringDapr(config);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds the DI graph the same way <see cref="BuildProvider"/> does but
    /// for a caller-chosen <see cref="SpringHostRole"/> so the host-role gate
    /// (ADR-0052) can be exercised directly.
    /// </summary>
    private static ServiceProvider BuildProvider(SpringHostRole role)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(Substitute.For<IActorProxyFactory>());
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase($"DiTest_{Guid.NewGuid():N}"));
        services.AddCvoyaSpringDapr(config, role);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// ADR-0052: the four worker-only execution hosted services
    /// (<see cref="Cvoya.Spring.Dapr.Execution.AgentVolumeManager"/>,
    /// <see cref="Cvoya.Spring.Dapr.Execution.PersistentAgentRegistry"/>,
    /// <see cref="Cvoya.Spring.Dapr.Execution.EphemeralAgentRegistry"/>,
    /// <c>ContainerHealthMetricsService</c>) register as
    /// <see cref="IHostedService"/> when the host composes with
    /// <see cref="SpringHostRole.ExecutionHost"/>.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_ExecutionHost_RegistersExecutionHostedServices()
    {
        using var provider = BuildProvider(SpringHostRole.ExecutionHost);

        var hosted = provider.GetServices<IHostedService>().ToList();

        hosted.ShouldContain(s => s is Cvoya.Spring.Dapr.Execution.AgentVolumeManager);
        hosted.ShouldContain(s => s is Cvoya.Spring.Dapr.Execution.PersistentAgentRegistry);
        hosted.ShouldContain(s => s is Cvoya.Spring.Dapr.Execution.EphemeralAgentRegistry);
        hosted.ShouldContain(s => s is Cvoya.Spring.Dapr.Execution.ContainerHealthMetricsService);
    }

    /// <summary>
    /// ADR-0052: the four worker-only execution hosted services do NOT
    /// register as <see cref="IHostedService"/> when the host composes with
    /// <see cref="SpringHostRole.HttpFrontDoor"/> — the API host serves the
    /// REST surface but does not own delegated-execution supervision.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_HttpFrontDoor_DoesNotRegisterExecutionHostedServices()
    {
        using var provider = BuildProvider(SpringHostRole.HttpFrontDoor);

        var hosted = provider.GetServices<IHostedService>().ToList();

        hosted.ShouldNotContain(s => s is Cvoya.Spring.Dapr.Execution.AgentVolumeManager);
        hosted.ShouldNotContain(s => s is Cvoya.Spring.Dapr.Execution.PersistentAgentRegistry);
        hosted.ShouldNotContain(s => s is Cvoya.Spring.Dapr.Execution.EphemeralAgentRegistry);
        hosted.ShouldNotContain(s => s is Cvoya.Spring.Dapr.Execution.ContainerHealthMetricsService);
    }

    /// <summary>
    /// ADR-0052: the DI singletons backing the gated execution hosted
    /// services stay registered unconditionally on both host roles. The API
    /// host resolves <see cref="Cvoya.Spring.Dapr.Execution.PersistentAgentRegistry"/>
    /// (and the other registry singletons) as plain singletons; gating only
    /// the <c>AddHostedService</c> wrappers must not break that resolution.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_HttpFrontDoor_ExecutionSingletonsStillResolve()
    {
        using var provider = BuildProvider(SpringHostRole.HttpFrontDoor);

        provider.GetService<Cvoya.Spring.Dapr.Execution.AgentVolumeManager>()
            .ShouldNotBeNull();
        provider.GetService<Cvoya.Spring.Dapr.Execution.PersistentAgentRegistry>()
            .ShouldNotBeNull();
        provider.GetService<Cvoya.Spring.Dapr.Execution.EphemeralAgentRegistry>()
            .ShouldNotBeNull();
    }

    /// <summary>
    /// ADR-0052 §2 / PR 2 of #2611 (#2614): the <c>McpServer</c> hosted
    /// service (port listener + in-process session store) runs in exactly
    /// one host — the worker — so there is one session authority. It
    /// registers as an <see cref="IHostedService"/> under
    /// <see cref="SpringHostRole.ExecutionHost"/> only, NOT under
    /// <see cref="SpringHostRole.HttpFrontDoor"/>.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_McpServer_RegistersAsHostedServiceUnderExecutionHostOnly()
    {
        using var executionProvider = BuildProvider(SpringHostRole.ExecutionHost);
        executionProvider.GetServices<IHostedService>().ToList().ShouldContain(
            s => s is Cvoya.Spring.Dapr.Mcp.McpServer,
            "McpServer must register as an IHostedService under ExecutionHost — " +
            "ADR-0052 §2 places the single session authority on the worker.");

        using var frontDoorProvider = BuildProvider(SpringHostRole.HttpFrontDoor);
        frontDoorProvider.GetServices<IHostedService>().ToList().ShouldNotContain(
            s => s is Cvoya.Spring.Dapr.Mcp.McpServer,
            "McpServer must NOT register as an IHostedService under HttpFrontDoor — " +
            "ADR-0052 §2 keeps the session authority worker-only.");
    }

    /// <summary>
    /// ADR-0052 §1: the <c>McpServer</c> / <c>IMcpServer</c> DI singletons
    /// stay registered unconditionally on both host roles (OpenAPI doc-gen,
    /// the latent <c>A2AExecutionDispatcher</c> singleton). Only the
    /// <c>AddHostedService</c> wrapper is execution-host-gated.
    /// </summary>
    [Theory]
    [InlineData(SpringHostRole.HttpFrontDoor)]
    [InlineData(SpringHostRole.ExecutionHost)]
    public void AddCvoyaSpringDapr_McpServerSingleton_ResolvesUnderBothRoles(
        SpringHostRole role)
    {
        using var provider = BuildProvider(role);

        provider.GetService<Cvoya.Spring.Dapr.Mcp.McpServer>().ShouldNotBeNull();
        provider.GetService<Cvoya.Spring.Core.Execution.IMcpServer>().ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringDapr_RegistersMessageRouter()
    {
        using var provider = BuildProvider();

        var router = provider.GetService<MessageRouter>();

        router.ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringDapr_RegistersDirectoryService()
    {
        using var provider = BuildProvider();

        var directoryService = provider.GetService<IDirectoryService>();

        directoryService.ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringDapr_RegistersExecutionDispatcher()
    {
        using var provider = BuildProvider();

        var dispatcher = provider.GetService<IExecutionDispatcher>();

        dispatcher.ShouldNotBeNull();
        dispatcher.ShouldBeOfType<Cvoya.Spring.Dapr.Execution.A2AExecutionDispatcher>();
    }

    /// <summary>
    /// ADR-0051: <c>AddCvoyaSpringDapr</c> registers
    /// <see cref="Cvoya.Spring.Dapr.Skills.SvMessagingSkillRegistry"/> into the
    /// <see cref="ISkillRegistry"/> set so the single platform MCP server
    /// serves <c>sv.messaging.send</c> / <c>sv.messaging.multicast</c>
    /// alongside every other <c>sv.*</c> tool. Because the tools live in the
    /// <c>sv</c> namespace, the effective-grant resolver's platform tier
    /// surfaces them for every agent / unit subject — that is the default
    /// grant seeding existing agents rely on.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_RegistersMessagingSkillRegistry()
    {
        using var provider = BuildProvider();

        var registries = provider.GetServices<ISkillRegistry>();
        var messaging = registries.OfType<Cvoya.Spring.Dapr.Skills.SvMessagingSkillRegistry>()
            .SingleOrDefault();

        messaging.ShouldNotBeNull();

        var toolNames = messaging.GetToolDefinitions().Select(t => t.Name).ToArray();
        toolNames.ShouldContain("sv.messaging.send");
        toolNames.ShouldContain("sv.messaging.multicast");
    }

    /// <summary>
    /// Regression test for #261 — updated for the #616 startup configuration
    /// validator. Configuration without <c>ConnectionStrings:SpringDb</c> and
    /// without a pre-registered <see cref="DbContextOptions{SpringDbContext}"/>
    /// must abort host startup with a clear message rather than deferring the
    /// failure to the first EF query. The throw now happens at
    /// <c>StartAsync</c> time inside <see cref="Cvoya.Spring.Dapr.Configuration.StartupConfigurationValidator"/>
    /// instead of at <c>AddCvoyaSpringDapr</c> time.
    /// </summary>
    [Fact]
    public async Task AddCvoyaSpringDapr_NoConnectionStringAndNoPreRegisteredDbContext_ValidatorThrows()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // #639 — the validator now runs every mandatory requirement at
        // StartAsync. The secrets requirement is satisfied by the
        // SPRING_SECRETS_AES_KEY env var set process-wide via this test
        // assembly's SecretsTestEnvironmentInitializer; that leaves the
        // missing DB connection string as the only failing mandatory
        // requirement, so the assertion does not have to untangle an
        // AggregateException of multiple mandatory failures.
        // #2518 — Dispatcher:BaseUrl is now mandatory too. Set a valid
        // URL via in-memory configuration so the missing DB connection
        // string is the only failing mandatory requirement, keeping this
        // test focused on the #261 regression (single InvalidOperationException
        // instead of an AggregateException covering DB + dispatcher).
        // #2597 — CallbackBaseUrl:BaseUrl is now mandatory too; set a
        // valid URL for the same reason.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dispatcher:BaseUrl"] = "http://spring-dispatcher.test/",
                ["Dispatcher:BearerToken"] = "test-token",
                ["CallbackBaseUrl:BaseUrl"] = "http://spring-caddy.test:8443/",
            })
            .Build();
        // IConfiguration needs to be present in DI for the requirement's
        // constructor injection; AddCvoyaSpringDapr does not register it
        // itself (the host's WebApplicationBuilder does).
        services.AddSingleton<IConfiguration>(config);

        services.AddCvoyaSpringDapr(config);

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<Cvoya.Spring.Dapr.Configuration.StartupConfigurationValidator>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => validator.StartAsync(TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("ConnectionStrings:SpringDb");
    }

    // ADR-0038 Chunk 2a: launcher DI registration moved out of
    // AddCvoyaSpringDapr into AddCvoyaSpringAgentRuntimes (in the
    // Cvoya.Spring.AgentRuntimes project). The equivalent regression
    // lives there under
    // Cvoya.Spring.AgentRuntimes.Tests.LauncherRegistrationTests.

    /// <summary>
    /// Complements the regression test above: when the test harness
    /// pre-registers its own <see cref="SpringDbContext"/> (e.g. with
    /// <c>UseInMemoryDatabase</c>), <c>AddCvoyaSpringDapr</c> must respect
    /// that override and NOT throw, even when no connection string is set.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_NoConnectionStringButPreRegisteredDbContext_Succeeds()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase($"DiTest_{Guid.NewGuid():N}"));

        Should.NotThrow(() => services.AddCvoyaSpringDapr(config));
    }

    /// <summary>
    /// #676: <c>AddCvoyaSpringDapr</c> must register the OSS file-system
    /// skill-bundle adapter as an enumerable
    /// <see cref="Core.Tenancy.ITenantSeedProvider"/>. Mirrors the
    /// single-host-owner pattern of <see cref="Cvoya.Spring.Dapr.Data.DatabaseMigrator"/>:
    /// the seed provider is part of the shared DI graph, but the
    /// hosted bootstrap service that consumes it is opt-in via
    /// <see cref="ServiceCollectionExtensions.AddCvoyaSpringDefaultTenantBootstrap"/>.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_RegistersFileSystemSkillBundleSeedProvider()
    {
        using var provider = BuildProvider();

        var seedProviders = provider.GetServices<Core.Tenancy.ITenantSeedProvider>().ToList();

        seedProviders.ShouldContain(p => p is Cvoya.Spring.Dapr.Skills.FileSystemSkillBundleSeedProvider);
    }

    /// <summary>
    /// #676 (mirrors the #305 invariant for the migrator):
    /// <c>AddCvoyaSpringDapr</c> on its own MUST NOT register the
    /// bootstrap as a hosted service, otherwise both the API and Worker
    /// hosts (which both call <c>AddCvoyaSpringDapr</c>) would race on
    /// the seed pass. Bootstrap registration is opt-in via
    /// <see cref="ServiceCollectionExtensions.AddCvoyaSpringDefaultTenantBootstrap"/>
    /// from the single host that owns it (the Worker in OSS).
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_DoesNotRegisterDefaultTenantBootstrap()
    {
        using var provider = BuildProvider();

        var hosted = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();
        hosted.ShouldNotContain(s => s is Cvoya.Spring.Dapr.Tenancy.DefaultTenantBootstrapService);
    }

    /// <summary>
    /// The opt-in extension introduced for #676 must register the bootstrap
    /// service as a hosted service so the host that calls it actually runs
    /// the seed pass on startup.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDefaultTenantBootstrap_RegistersHostedService()
    {
        var services = new ServiceCollection();

        services.AddCvoyaSpringDefaultTenantBootstrap();

        services.ShouldContain(d =>
            d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && d.ImplementationType == typeof(Cvoya.Spring.Dapr.Tenancy.DefaultTenantBootstrapService));
    }

    /// <summary>
    /// #676: <c>TenancyOptions</c> binding lives in
    /// <c>AddCvoyaSpringDapr</c> so non-bootstrapping hosts (the API)
    /// can still observe the configured value.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_BindsTenancyOptions()
    {
        using var provider = BuildProvider();

        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Cvoya.Spring.Dapr.Tenancy.TenancyOptions>>();

        options.Value.BootstrapDefaultTenant.ShouldBeTrue();
    }

    /// <summary>
    /// #969: when <c>Skills:PackagesRoot</c> is not set, the Dapr-level
    /// post-configure must bridge <c>SkillBundleOptions.PackagesRoot</c>
    /// to the shared <c>Packages:Root</c> key. Without this the Worker
    /// host (which owns default-tenant bootstrap) runs
    /// <see cref="Cvoya.Spring.Dapr.Skills.FileSystemSkillBundleSeedProvider"/>
    /// with a null root and silently binds nothing.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_SkillBundlePackagesRoot_FallsBackToSharedPackagesRoot()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Packages:Root"] = "/packages",
            })
            .Build();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(Substitute.For<IActorProxyFactory>());
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase($"DiTest_{Guid.NewGuid():N}"));

        services.AddCvoyaSpringDapr(config);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Cvoya.Spring.Dapr.Skills.SkillBundleOptions>>();

        options.Value.PackagesRoot.ShouldBe("/packages");
    }

    /// <summary>
    /// #969: the fallback must prefer an explicit
    /// <c>Skills:PackagesRoot</c> over the shared <c>Packages:Root</c>
    /// so operators who already set the specific key keep control.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_SkillBundlePackagesRoot_PrefersExplicitSkillsKey()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Skills:PackagesRoot"] = "/explicit",
                ["Packages:Root"] = "/shared",
            })
            .Build();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(Substitute.For<IActorProxyFactory>());
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase($"DiTest_{Guid.NewGuid():N}"));

        services.AddCvoyaSpringDapr(config);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Cvoya.Spring.Dapr.Skills.SkillBundleOptions>>();

        options.Value.PackagesRoot.ShouldBe("/explicit");
    }

    /// <summary>
    /// #969: when neither key is set and no env var is present, the
    /// fallback leaves <c>PackagesRoot</c> null so the seed provider logs
    /// its misconfiguration warning and returns instead of enumerating
    /// a wrong path.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_SkillBundlePackagesRoot_NullWhenUnconfigured()
    {
        var previousEnv = System.Environment.GetEnvironmentVariable("SPRING_PACKAGES_ROOT");
        System.Environment.SetEnvironmentVariable("SPRING_PACKAGES_ROOT", null);
        try
        {
            using var provider = BuildProvider();
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Cvoya.Spring.Dapr.Skills.SkillBundleOptions>>();

            options.Value.PackagesRoot.ShouldBeNull();
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SPRING_PACKAGES_ROOT", previousEnv);
        }
    }

    // -----------------------------------------------------------------------
    // Regression tests for #568 — RemoveDaprWorkflowWorker strip pattern
    // -----------------------------------------------------------------------

    /// <summary>
    /// Regression test for #568: <c>RemoveDaprWorkflowWorker</c> must remove
    /// every <c>IHostedService</c> registration whose implementation type lives
    /// under the <c>Dapr.Workflow</c> namespace while leaving every other
    /// hosted service intact.
    /// </summary>
    /// <remarks>
    /// This test fails without the strip (a Dapr.Workflow IHostedService is
    /// present) and passes with it (none remain after the call). The
    /// assertion is deliberately implementation-agnostic: it checks the
    /// namespace prefix rather than the concrete type name so SDK upgrades
    /// that rename the class still keep the test meaningful.
    /// </remarks>
    [Fact]
    public void RemoveDaprWorkflowWorker_AfterAddDaprWorkflow_RemovesWorkerHostedService()
    {
        // Arrange — register the workflow worker the same way test harnesses do.
        var services = new ServiceCollection();
        services.AddDaprWorkflow(options => { });

        // Baseline: AddDaprWorkflow registers at least one Dapr.Workflow-
        // namespaced IHostedService (the WorkflowWorker background service).
        var before = services
            .Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType?.FullName?.StartsWith(
                    "Dapr.Workflow.", StringComparison.Ordinal) == true)
            .ToList();
        before.ShouldNotBeEmpty(
            "AddDaprWorkflow must register at least one Dapr.Workflow IHostedService");

        // Act
        services.RemoveDaprWorkflowWorker();

        // Assert — no Dapr.Workflow IHostedService remains.
        var after = services
            .Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType?.FullName?.StartsWith(
                    "Dapr.Workflow.", StringComparison.Ordinal) == true)
            .ToList();
        after.ShouldBeEmpty(
            "RemoveDaprWorkflowWorker must remove all Dapr.Workflow IHostedService registrations");
    }

    /// <summary>
    /// The strip must be idempotent: calling <c>RemoveDaprWorkflowWorker</c>
    /// a second time (e.g. after a subsequent <c>AddDaprWorkflow</c> re-adds
    /// the worker) must leave zero Dapr.Workflow workers in the collection.
    /// This regression guards the double-strip pattern used in
    /// <c>AuthHandlerRoleClaimsTests</c> where the factory calls
    /// <c>AddDaprWorkflow</c> after the first strip.
    /// </summary>
    [Fact]
    public void RemoveDaprWorkflowWorker_CalledTwice_IsIdempotent()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddDaprWorkflow(options => { });

        // Act — strip, re-add, strip again (mirrors AuthHandlerRoleClaimsTests).
        services.RemoveDaprWorkflowWorker();
        services.AddDaprWorkflow(options => { });
        services.RemoveDaprWorkflowWorker();

        // Assert
        var remaining = services
            .Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType?.FullName?.StartsWith(
                    "Dapr.Workflow.", StringComparison.Ordinal) == true)
            .ToList();
        remaining.ShouldBeEmpty(
            "RemoveDaprWorkflowWorker must be idempotent and strip all Dapr.Workflow workers after a re-add");
    }

    /// <summary>
    /// The strip must preserve the <c>DaprWorkflowClient</c> registration so
    /// endpoint code that injects the workflow client continues to resolve
    /// after the worker is removed. This is the load-bearing guarantee that
    /// allows test hosts to strip the worker without breaking DI resolution.
    /// </summary>
    [Fact]
    public void RemoveDaprWorkflowWorker_PreservesDaprWorkflowClient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddDaprWorkflow(options => { });

        // Act
        services.RemoveDaprWorkflowWorker();

        // Assert — DaprWorkflowClient must still be resolvable.
        services.ShouldContain(
            d => d.ServiceType == typeof(DaprWorkflowClient),
            "DaprWorkflowClient must remain registered after RemoveDaprWorkflowWorker");
    }

    /// <summary>
    /// Calling <c>RemoveDaprWorkflowWorker</c> when no <c>AddDaprWorkflow</c>
    /// has been called must be a no-op (idempotent on an empty or
    /// workflow-free collection).
    /// </summary>
    [Fact]
    public void RemoveDaprWorkflowWorker_WhenNoWorkerRegistered_IsNoOp()
    {
        // Arrange — no AddDaprWorkflow.
        var services = new ServiceCollection();
        var countBefore = services.Count;

        // Act — must not throw.
        Should.NotThrow(() => services.RemoveDaprWorkflowWorker());

        // Assert — collection is unchanged.
        services.Count.ShouldBe(countBefore);
    }
}
