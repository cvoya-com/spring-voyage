// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.OAuth;

using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.DependencyInjection;
using Cvoya.Spring.Core.Secrets;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

public class OAuthServiceRegistrationTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?>? extra = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["GitHub:AppId"] = "12345",
            ["GitHub:PrivateKeyPem"] = TestPemKey.Value,
            ["GitHub:WebhookSecret"] = "test-secret",
            ["GitHub:InstallationId"] = "67890",
            ["GitHub:OAuth:ClientId"] = "oauth-client",
            ["GitHub:OAuth:ClientSecret"] = "oauth-secret",
            ["GitHub:OAuth:RedirectUri"] = "https://example.com/cb",
            ["GitHub:OAuth:Scopes:0"] = "repo",
            ["GitHub:OAuth:Scopes:1"] = "user:email",
        };
        if (extra is not null)
        {
            foreach (var kv in extra)
            {
                config[kv.Key] = kv.Value;
            }
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        // The OAuth service depends on ISecretStore; tests register a fake
        // since the real Dapr-backed store is not available here.
        services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddCvoyaSpringConnectorGitHub(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_BindsOAuthOptions()
    {
        using var provider = BuildProvider();

        var options = provider.GetRequiredService<IOptions<GitHubOAuthOptions>>().Value;

        options.ClientId.ShouldBe("oauth-client");
        options.ClientSecret.ShouldBe("oauth-secret");
        options.RedirectUri.ShouldBe("https://example.com/cb");
        options.Scopes.ShouldContain("repo");
        options.Scopes.ShouldContain("user:email");
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersOAuthStores_AndService()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IOAuthStateStore>().ShouldBeOfType<InMemoryOAuthStateStore>();
        provider.GetRequiredService<IOAuthSessionStore>().ShouldBeOfType<InMemoryOAuthSessionStore>();
        provider.GetRequiredService<IGitHubOAuthHttpClient>().ShouldBeOfType<GitHubOAuthHttpClient>();
        provider.GetRequiredService<IGitHubUserFetcher>().ShouldBeOfType<OctokitGitHubUserFetcher>();
        provider.GetRequiredService<IGitHubOAuthService>().ShouldBeOfType<GitHubOAuthService>();
        provider.GetRequiredService<IGitHubOAuthClientFactory>().ShouldBeOfType<GitHubOAuthClientFactory>();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_PreregisteredStore_NotOverridden()
    {
        var custom = Substitute.For<IOAuthStateStore>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:AppId"] = "1",
            }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton(custom);
        services.AddCvoyaSpringConnectorGitHub(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOAuthStateStore>().ShouldBeSameAs(custom);
    }
}