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
/// injects (#2380), not through a platform <c>github.*</c> tool. Two
/// structural exceptions are pinned here, each a read of connector-emitted
/// state with no equivalent on the <c>gh</c> CLI:
/// <list type="bullet">
///   <item><description><c>github.get_installation_token</c> (#2704) — read of the
///     platform-managed credential the model previously hallucinated an
///     HTTP URL to fetch.</description></item>
///   <item><description><c>github.describe_inbound_contract</c> (#2676) — read of the
///     connector-emitted inbound envelope and intent vocabulary; without
///     it every GitHub-bound package would have to re-paste ~4 KB of
///     prompt text.</description></item>
/// </list>
/// Any third <c>github.*</c> tool re-opens the broader #2384 / #2383
/// decision and must land in its own PR with the rationale documented
/// alongside the test update.
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
        // tool set must contain exactly the two pinned github.* entries —
        // get_installation_token (#2704) and describe_inbound_contract
        // (#2676). Any third github.* entry re-opens the #2384 / #2383
        // design decision and must land in its own PR with the rationale
        // documented alongside this test update.
        var githubTools = provider.GetServices<ISkillRegistry>()
            .SelectMany(r => r.GetToolDefinitions())
            .Where(d => d.Name.StartsWith("github.", StringComparison.Ordinal))
            .Select(d => d.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        githubTools.ShouldBe(new[]
        {
            GitHubSkillRegistry.DescribeInboundContractTool,
            GitHubSkillRegistry.GetInstallationTokenTool,
        },
            "Only the two pinned github.* tools may surface from the platform MCP " +
            "— get_installation_token (#2704) and describe_inbound_contract (#2676). " +
            "Both are reads of connector-emitted state with no gh-CLI equivalent. " +
            "Any third github.* tool re-opens the #2384 / #2383 decision.");
    }
}
