// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub;
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
        _executor = new GitHubSkillToolExecutor(loggerFactory);
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

    [Fact]
    public async Task ExecuteAsync_ReturnsNonErrorStubResultCarryingToolUseId()
    {
        var call = new ToolCall(
            "toolu_42",
            "github_read_file",
            JsonSerializer.SerializeToElement(new { owner = "a", repo = "b", path = "c" }));

        var result = await _executor.ExecuteAsync(call, TestContext.Current.CancellationToken);

        result.IsError.Should().BeFalse();
        result.ToolUseId.Should().Be("toolu_42");
        result.Content.Should().Contain("github_read_file");
        result.Content.Should().Contain("not yet wired");
    }
}