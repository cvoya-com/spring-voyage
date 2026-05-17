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
/// Regression for issues #2384 / #2383: the GitHub connector's DI extension
/// must NOT register an <see cref="ISkillRegistry"/>. Without one, the MCP
/// server's <c>tools/list</c> response cannot include any <c>github.*</c>
/// entry — agents bound to a unit with a GitHub binding reach GitHub through
/// the in-container <c>gh</c> / <c>git</c> CLIs using the credentials the
/// runtime-context contributor injects (#2380), not through the platform MCP
/// surface. A drift here — a returning workload skill, a stub registry — is
/// what this test exists to catch loudly.
/// </summary>
public class GitHubConnectorDoesNotRegisterMcpToolsTests
{
    [Fact]
    public void AddCvoyaSpringConnectorGitHub_DoesNotRegisterAnyIskillRegistry()
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

        // No ISkillRegistry instance should be resolvable from a service
        // provider whose only contributor is the GitHub connector. Any
        // registration here would surface in the MCP server's tools/list
        // because the server enumerates every IEnumerable<ISkillRegistry>.
        var registries = provider.GetServices<ISkillRegistry>().ToArray();
        registries.ShouldBeEmpty(
            "GitHub connector must not register any ISkillRegistry — agents " +
            "use the in-container gh / git CLIs through the runtime-context " +
            "contributor (#2380); platform MCP must not surface github.* tools.");
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_DoesNotRegisterAnyToolDefinitionsUnderTheGithubNamespace()
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

        // Defence in depth: even if some future contributor accidentally
        // registers an ISkillRegistry under the github namespace, the merged
        // tool set must surface zero github.* entries. We assert across every
        // registered registry because the MCP server's tools/list does the
        // same fan-out.
        var allNames = provider.GetServices<ISkillRegistry>()
            .SelectMany(r => r.GetToolDefinitions())
            .Select(d => d.Name)
            .ToArray();

        allNames.Where(n => n.StartsWith("github.", StringComparison.Ordinal))
            .ShouldBeEmpty(
                "No github.* tool may surface from the platform MCP — agents " +
                "use the in-container gh / git CLIs (#2380).");
    }
}
