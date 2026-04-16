// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.DependencyInjection;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
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
    /// Regression test for #261. Configuration without
    /// <c>ConnectionStrings:SpringDb</c> and without a pre-registered
    /// <see cref="DbContextOptions{SpringDbContext}"/> must throw an
    /// <see cref="InvalidOperationException"/> with a clear message at
    /// <c>AddCvoyaSpringDapr</c> time — NOT defer the failure to the
    /// first EF query (the original bug: unit creation returning 500
    /// with "No database provider has been configured for this DbContext").
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_NoConnectionStringAndNoPreRegisteredDbContext_Throws()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        var ex = Should.Throw<InvalidOperationException>(() =>
            services.AddCvoyaSpringDapr(config));

        ex.Message.ShouldContain("ConnectionStrings:SpringDb");
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