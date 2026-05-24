// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.DependencyInjection;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Regression for issues #2384 / #2383: the GitHub connector's MCP surface
/// must stay narrow. The wider rule still stands — agents bound to a unit
/// with a GitHub binding reach GitHub through the in-container <c>gh</c> /
/// <c>git</c> CLIs against the credentials the runtime-context contributor
/// injects (#2380), not through a platform <c>github.*</c> tool. #2704
/// landed exactly one structural exception: <c>github.get_installation_token</c>,
/// a read of platform-managed state the model otherwise hallucinated URLs
/// for. This test exists to ensure no second exception sneaks in.
/// </summary>
public class GitHubConnectorDoesNotRegisterMcpToolsTests
{
    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersExactlyOneIskillRegistry()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:AppId"] = "12345",
                ["GitHub:PrivateKeyPem"] = TestPemKey.Value,
                ["GitHub:WebhookSecret"] = "test-secret",
                ["GitHub:InstallationId"] = "67890",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCvoyaSpringConnectorGitHub(configuration);

        using var provider = services.BuildServiceProvider();

        var registries = provider.GetServices<ISkillRegistry>().ToArray();
        registries.ShouldHaveSingleItem();
        registries[0].ShouldBeOfType<GitHubSkillRegistry>(
            "GitHub connector must register exactly one ISkillRegistry — the narrow " +
            "GitHubSkillRegistry that exposes only github.get_installation_token (#2704).");
    }

    [Fact]
    public void GitHubConnector_ExposesOnlyTheTokenTool_OnTheGithubNamespace()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:AppId"] = "12345",
                ["GitHub:PrivateKeyPem"] = TestPemKey.Value,
                ["GitHub:WebhookSecret"] = "test-secret",
                ["GitHub:InstallationId"] = "67890",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCvoyaSpringConnectorGitHub(configuration);

        using var provider = services.BuildServiceProvider();

        // Defence in depth: across every registered registry the merged
        // tool set must contain exactly one github.* entry — the token
        // tool. Any second github.* entry re-opens the #2384 / #2383
        // design decision and must land as its own PR with a new ADR.
        var githubTools = provider.GetServices<ISkillRegistry>()
            .SelectMany(r => r.GetToolDefinitions())
            .Where(d => d.Name.StartsWith("github.", StringComparison.Ordinal))
            .Select(d => d.Name)
            .ToArray();

        githubTools.ShouldBe(new[] { GitHubSkillRegistry.GetInstallationTokenTool },
            "Only github.get_installation_token may surface from the platform MCP " +
            "(#2704). Any second github.* tool re-opens the wider #2384 / #2383 " +
            "decision and must land alongside an ADR amendment.");
    }
}
