// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.DependencyInjection;
using Cvoya.Spring.Connector.GitHub.RateLimit;
using Cvoya.Spring.Connector.GitHub.Webhooks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

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

        options.ShouldNotBeNull();
        options.AppId.ShouldBe(12345);
        options.WebhookSecret.ShouldBe("test-secret");
        options.InstallationId.ShouldBe(67890);
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubAppAuth()
    {
        using var provider = BuildProvider();

        var auth = provider.GetRequiredService<GitHubAppAuth>();

        auth.ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubWebhookHandler()
    {
        using var provider = BuildProvider();

        var handler = provider.GetRequiredService<GitHubWebhookHandler>();

        handler.ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubSkillRegistry()
    {
        using var provider = BuildProvider();

        var registry = provider.GetRequiredService<GitHubSkillRegistry>();

        registry.ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubConnector()
    {
        using var provider = BuildProvider();

        var connector = provider.GetRequiredService<GitHubConnector>();

        connector.ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersRateLimitTracker()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IGitHubRateLimitTracker>().ShouldNotBeNull();
        provider.GetRequiredService<GitHubRetryOptions>().ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_BindsRetryOptions_FromConfiguration()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["GitHub:AppId"] = "12345",
            ["GitHub:PrivateKeyPem"] = "test-key",
            ["GitHub:WebhookSecret"] = "test-secret",
            ["GitHub:Retry:MaxRetries"] = "7",
            ["GitHub:Retry:PreflightSafetyThreshold"] = "42"
        });

        var options = provider.GetRequiredService<GitHubRetryOptions>();
        options.MaxRetries.ShouldBe(7);
        options.PreflightSafetyThreshold.ShouldBe(42);
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

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCvoyaSpringConnectorGitHub(configuration);

        var connector = services.BuildServiceProvider().GetRequiredService<GitHubConnector>();
        var customRegistry = new GitHubSkillRegistry(connector, Substitute.For<ILoggerFactory>());

        var servicesWithOverride = new ServiceCollection();
        servicesWithOverride.AddLogging();
        servicesWithOverride.AddSingleton(customRegistry);
        servicesWithOverride.AddCvoyaSpringConnectorGitHub(configuration);

        using var provider = servicesWithOverride.BuildServiceProvider();

        var resolved = provider.GetRequiredService<GitHubSkillRegistry>();
        resolved.ShouldBeSameAs(customRegistry);
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_ReturnsSameServiceCollection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();

        var result = services.AddCvoyaSpringConnectorGitHub(configuration);

        result.ShouldBeSameAs(services);
    }
}