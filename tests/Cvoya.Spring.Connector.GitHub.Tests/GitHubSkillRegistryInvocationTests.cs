// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Core.Skills;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Xunit;

/// <summary>
/// Verifies that <see cref="GitHubSkillRegistry"/> correctly implements
/// <see cref="ISkillRegistry"/> contract semantics independently of the
/// Octokit dispatch path (which is covered by the per-skill tests).
/// </summary>
public class GitHubSkillRegistryInvocationTests
{
    private readonly GitHubSkillRegistry _registry;

    public GitHubSkillRegistryInvocationTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var options = new GitHubConnectorOptions();
        var auth = new GitHubAppAuth(options, loggerFactory);
        var webhookHandler = new GitHubWebhookHandler(options, loggerFactory);
        var signatureValidator = new WebhookSignatureValidator();
        var connector = new GitHubConnector(auth, webhookHandler, signatureValidator, options, loggerFactory);
        _registry = new GitHubSkillRegistry(connector, loggerFactory);
    }

    [Fact]
    public void Name_IsGithub()
    {
        _registry.Name.Should().Be("github");
    }

    [Fact]
    public async Task InvokeAsync_UnknownTool_ThrowsSkillNotFoundException()
    {
        var act = () => _registry.InvokeAsync(
            "github_not_a_tool",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);

        await act.Should().ThrowAsync<SkillNotFoundException>()
            .Where(e => e.ToolName == "github_not_a_tool");
    }

    [Fact]
    public void GetToolDefinitions_CoversEveryDispatcher()
    {
        var tools = _registry.GetToolDefinitions().Select(t => t.Name).ToHashSet();

        tools.Should().BeEquivalentTo(new[]
        {
            "github_create_branch",
            "github_create_pull_request",
            "github_comment_on_issue",
            "github_comment_on_pull_request",
            "github_read_file",
            "github_write_file",
            "github_delete_file",
            "github_list_files",
            "github_get_issue_details",
            "github_get_pull_request_diff",
            "github_manage_labels",
        });
    }
}