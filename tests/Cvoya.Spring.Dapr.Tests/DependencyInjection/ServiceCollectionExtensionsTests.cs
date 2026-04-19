// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.DependencyInjection;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    public void AddCvoyaSpringDapr_RegistersOrchestrationStrategies()
    {
        using var provider = BuildProvider();

        var ai = provider.GetKeyedService<IOrchestrationStrategy>("ai");
        var workflow = provider.GetKeyedService<IOrchestrationStrategy>("workflow");

        ai.ShouldNotBeNull();
        ai.ShouldBeOfType<AiOrchestrationStrategy>();
        workflow.ShouldNotBeNull();
        workflow.ShouldBeOfType<WorkflowOrchestrationStrategy>();
    }

    /// <summary>
    /// #389: the label-routed strategy is a scoped keyed registration (it
    /// depends on <c>IUnitPolicyRepository</c>, which is scoped). Resolving
    /// from a scope must produce an instance; the unkeyed default remains
    /// AI to preserve backward compatibility.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_RegistersLabelRoutedStrategyUnderItsKey()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var labelRouted = scope.ServiceProvider.GetKeyedService<IOrchestrationStrategy>("label-routed");
        labelRouted.ShouldNotBeNull();
        labelRouted.ShouldBeOfType<LabelRoutedOrchestrationStrategy>();
    }

    /// <summary>
    /// Regression test for #312. <c>UnitActor</c> is constructed by the Dapr
    /// actor runtime via plain DI and takes an unkeyed
    /// <see cref="IOrchestrationStrategy"/>. Without an unkeyed default
    /// registration, actor activation fails with
    /// "Unable to resolve service for type 'IOrchestrationStrategy'". Ensure
    /// the unkeyed default resolves to <see cref="AiOrchestrationStrategy"/>
    /// and that the keyed registrations still resolve.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_RegistersUnkeyedOrchestrationStrategyDefault()
    {
        using var provider = BuildProvider();

        var unkeyed = provider.GetService<IOrchestrationStrategy>();
        var ai = provider.GetKeyedService<IOrchestrationStrategy>("ai");
        var workflow = provider.GetKeyedService<IOrchestrationStrategy>("workflow");

        unkeyed.ShouldNotBeNull();
        unkeyed.ShouldBeOfType<AiOrchestrationStrategy>();
        ai.ShouldNotBeNull();
        ai.ShouldBeOfType<AiOrchestrationStrategy>();
        workflow.ShouldNotBeNull();
        workflow.ShouldBeOfType<WorkflowOrchestrationStrategy>();
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
        var config = new ConfigurationBuilder().Build();
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

    [Fact]
    public void AddCvoyaSpringDapr_RegistersAllAgentToolLaunchers()
    {
        using var provider = BuildProvider();

        var launchers = provider.GetServices<IAgentToolLauncher>().ToList();
        var launchersByTool = launchers.ToDictionary(l => l.Tool, StringComparer.OrdinalIgnoreCase);

        launchersByTool.ShouldContainKey("claude-code");
        launchersByTool["claude-code"].ShouldBeOfType<ClaudeCodeLauncher>();

        launchersByTool.ShouldContainKey("codex");
        launchersByTool["codex"].ShouldBeOfType<CodexLauncher>();

        launchersByTool.ShouldContainKey("gemini");
        launchersByTool["gemini"].ShouldBeOfType<GeminiLauncher>();
    }

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
}