// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.DependencyInjection;
using Cvoya.Spring.Connector.GitHub.Webhooks;

using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

public class ServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?>? configValues = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? new Dictionary<string, string?>
            {
                ["GitHub:AppId"] = "12345",
                ["GitHub:PrivateKeyPem"] = "test-key",
                ["GitHub:WebhookSecret"] = "test-secret",
                ["GitHub:InstallationId"] = "67890"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCvoyaSpringConnectorGitHub(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubConnectorOptions()
    {
        using var provider = BuildProvider();

        var options = provider.GetRequiredService<GitHubConnectorOptions>();

        options.Should().NotBeNull();
        options.AppId.Should().Be(12345);
        options.WebhookSecret.Should().Be("test-secret");
        options.InstallationId.Should().Be(67890);
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubAppAuth()
    {
        using var provider = BuildProvider();

        var auth = provider.GetRequiredService<GitHubAppAuth>();

        auth.Should().NotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubWebhookHandler()
    {
        using var provider = BuildProvider();

        var handler = provider.GetRequiredService<GitHubWebhookHandler>();

        handler.Should().NotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubSkillRegistry()
    {
        using var provider = BuildProvider();

        var registry = provider.GetRequiredService<GitHubSkillRegistry>();

        registry.Should().NotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubConnector()
    {
        using var provider = BuildProvider();

        var connector = provider.GetRequiredService<GitHubConnector>();

        connector.Should().NotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_UsesTryAdd_DoesNotOverrideExistingRegistrations()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:AppId"] = "12345",
                ["GitHub:PrivateKeyPem"] = "test-key",
                ["GitHub:WebhookSecret"] = "test-secret"
            })
            .Build();

        var customRegistry = new GitHubSkillRegistry();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(customRegistry);
        services.AddCvoyaSpringConnectorGitHub(configuration);

        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<GitHubSkillRegistry>();
        resolved.Should().BeSameAs(customRegistry);
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_ReturnsSameServiceCollection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();

        var result = services.AddCvoyaSpringConnectorGitHub(configuration);

        result.Should().BeSameAs(services);
    }
}