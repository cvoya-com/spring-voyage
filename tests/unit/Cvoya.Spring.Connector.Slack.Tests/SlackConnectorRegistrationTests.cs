// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using Cvoya.Spring.Connector.Slack;
using Cvoya.Spring.Connector.Slack.DependencyInjection;
using Cvoya.Spring.Connectors;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Pins that <see cref="ServiceCollectionExtensions.AddCvoyaSpringConnectorSlack"/>
/// registers an <see cref="IConnectorType"/> with the expected slug —
/// the host-side iteration over registered connector types must
/// include Slack once this extension is called.
/// </summary>
public class SlackConnectorRegistrationTests
{
    [Fact]
    public void AddCvoyaSpringConnectorSlack_RegistersIConnectorType()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();
        services.AddCvoyaSpringConnectorSlack(configuration);

        using var provider = services.BuildServiceProvider();
        var connectorTypes = provider.GetServices<IConnectorType>();

        connectorTypes.ShouldContain(c => c.Slug == "slack");
        connectorTypes.ShouldContain(c => c.TypeId == SlackConnectorType.SlackTypeId);
        connectorTypes.First(c => c.Slug == "slack").BindingScope.ShouldBe(BindingScope.Tenant);
    }

    [Fact]
    public void AddCvoyaSpringConnectorSlack_RegistersSlackTenantBoundUserExtractor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();
        services.AddCvoyaSpringConnectorSlack(configuration);

        using var provider = services.BuildServiceProvider();
        var extractors = provider.GetServices<ITenantBoundUserExtractor>();

        extractors.ShouldContain(e => e.Handles("slack"));
    }
}
