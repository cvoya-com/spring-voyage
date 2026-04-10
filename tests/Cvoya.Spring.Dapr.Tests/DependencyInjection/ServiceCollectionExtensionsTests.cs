// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.DependencyInjection;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;
using global::Dapr.Actors.Client;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
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
        services.AddCvoyaSpringDapr(config);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddCvoyaSpringDapr_RegistersMessageRouter()
    {
        using var provider = BuildProvider();

        var router = provider.GetService<MessageRouter>();

        router.Should().NotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringDapr_RegistersDirectoryService()
    {
        using var provider = BuildProvider();

        var directoryService = provider.GetService<IDirectoryService>();

        directoryService.Should().NotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringDapr_RegistersOrchestrationStrategies()
    {
        using var provider = BuildProvider();

        var ai = provider.GetKeyedService<IOrchestrationStrategy>("ai");
        var workflow = provider.GetKeyedService<IOrchestrationStrategy>("workflow");
        var hybrid = provider.GetKeyedService<IOrchestrationStrategy>("hybrid");

        ai.Should().NotBeNull().And.BeOfType<AiOrchestrationStrategy>();
        workflow.Should().NotBeNull().And.BeOfType<WorkflowOrchestrationStrategy>();
        hybrid.Should().NotBeNull().And.BeOfType<HybridOrchestrationStrategy>();
    }

    [Fact]
    public void AddCvoyaSpringDapr_RegistersExecutionDispatchers()
    {
        using var provider = BuildProvider();

        var hosted = provider.GetKeyedService<IExecutionDispatcher>("hosted");
        var delegated = provider.GetKeyedService<IExecutionDispatcher>("delegated");

        hosted.Should().NotBeNull().And.BeOfType<Cvoya.Spring.Dapr.Execution.HostedExecutionDispatcher>();
        delegated.Should().NotBeNull().And.BeOfType<Cvoya.Spring.Dapr.Execution.DelegatedExecutionDispatcher>();
    }
}
