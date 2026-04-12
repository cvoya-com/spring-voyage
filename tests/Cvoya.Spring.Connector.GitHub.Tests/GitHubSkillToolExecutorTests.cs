// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub;
using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Core.Execution;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="GitHubSkillToolExecutor"/>.
/// </summary>
public class GitHubSkillToolExecutorTests
{
    private readonly GitHubSkillToolExecutor _executor;

    public GitHubSkillToolExecutorTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        // The connector is concrete and not easily mocked, but CanHandle does not touch it,
        // and the error-path tests only exercise input validation before any GitHub client is resolved.
        var options = new GitHubConnectorOptions
        {
            AppId = 0,
            PrivateKeyPem = string.Empty,
            WebhookSecret = string.Empty,
            InstallationId = null,
        };
        var auth = new GitHubAppAuth(options, loggerFactory);
        var webhookHandler = new GitHubWebhookHandler(loggerFactory);
        var skillRegistry = new GitHubSkillRegistry();
        var connector = new GitHubConnector(auth, webhookHandler, skillRegistry, options, loggerFactory);

        _executor = new GitHubSkillToolExecutor(connector, loggerFactory);
    }

    [Fact]
    public void CanHandle_ReturnsTrueForGitHubPrefixedTools()
    {
        _executor.CanHandle("github_read_file").Should().BeTrue();
        _executor.CanHandle("github_create_pull_request").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_ReturnsFalseForOtherTools()
    {
        _executor.CanHandle("kubernetes_apply").Should().BeFalse();
        _executor.CanHandle("slack_send_message").Should().BeFalse();
        _executor.CanHandle(string.Empty).Should().BeFalse();
    }

    /// <summary>
    /// Verifies that unknown github-prefixed tool names surface as <see cref="ToolResult.IsError"/>
    /// rather than throwing, so the tool-use loop can continue.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnknownGitHubTool_ReturnsErrorResult()
    {
        var call = new ToolCall(
            "toolu_unknown",
            "github_does_not_exist",
            JsonSerializer.SerializeToElement(new { }));

        var result = await _executor.ExecuteAsync(call, TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.ToolUseId.Should().Be("toolu_unknown");
        result.Content.Should().Contain("github_does_not_exist");
    }

    /// <summary>
    /// Verifies that missing required input fields surface as <see cref="ToolResult.IsError"/>
    /// rather than throwing, so the tool-use loop can continue.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MissingRequiredField_ReturnsErrorResult()
    {
        var call = new ToolCall(
            "toolu_bad",
            "github_read_file",
            JsonSerializer.SerializeToElement(new { owner = "a" })); // missing repo and path

        var result = await _executor.ExecuteAsync(call, TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.ToolUseId.Should().Be("toolu_bad");
    }
}