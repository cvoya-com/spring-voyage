// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch.Tests;

using Cvoya.Spring.Connector.WebSearch;
using Cvoya.Spring.Connector.WebSearch.DependencyInjection;
using Cvoya.Spring.Connector.WebSearch.Providers;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;

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
        // Stand in for dependencies normally contributed by the platform host.
        services.AddSingleton(Substitute.For<IUnitConnectorConfigStore>());
        services.AddSingleton(Substitute.For<ISecretResolver>());
        services.AddSingleton(Substitute.For<ITenantContext>());
        services.AddCvoyaSpringConnectorWebSearch(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Registers_WebSearchConnectorType_As_IConnectorType()
    {
        using var provider = BuildProvider();

        provider.GetServices<IConnectorType>().ShouldContain(c => c is WebSearchConnectorType);
    }

    [Fact]
    public void Registers_WebSearchSkillRegistry_As_ISkillRegistry()
    {
        using var provider = BuildProvider();

        provider.GetServices<ISkillRegistry>().ShouldContain(r => r is WebSearchSkillRegistry);
    }

    [Fact]
    public void Registers_DefaultBraveProvider()
    {
        using var provider = BuildProvider();

        var providers = provider.GetServices<IWebSearchProvider>().ToList();
        providers.Count.ShouldBeGreaterThanOrEqualTo(1);
        providers.ShouldContain(p => p is BraveSearchProvider);
    }
}
