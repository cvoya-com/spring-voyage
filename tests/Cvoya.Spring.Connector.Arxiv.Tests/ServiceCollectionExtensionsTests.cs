// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv.Tests;

using Cvoya.Spring.Connector.Arxiv;
using Cvoya.Spring.Connector.Arxiv.DependencyInjection;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

public class ServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // The connector type depends on IUnitConnectorConfigStore; swap in a
        // substitute so the DI graph resolves without needing the Dapr stack.
        services.AddSingleton(Substitute.For<IUnitConnectorConfigStore>());
        services.AddCvoyaSpringConnectorArxiv(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Registers_ArxivConnectorType_As_IConnectorType()
    {
        using var provider = BuildProvider();

        var connectorTypes = provider.GetServices<IConnectorType>().ToList();
        connectorTypes.ShouldContain(c => c is ArxivConnectorType);
    }

    [Fact]
    public void Registers_ArxivSkillRegistry_As_ISkillRegistry()
    {
        using var provider = BuildProvider();

        var registries = provider.GetServices<ISkillRegistry>().ToList();
        registries.ShouldContain(r => r is ArxivSkillRegistry);
    }

    [Fact]
    public void Registers_IArxivClient_Default()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IArxivClient>().ShouldNotBeNull();
    }
}