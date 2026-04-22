// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ClaudeCodeLauncher"/>.
/// </summary>
public class ClaudeCodeLauncherTests
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ClaudeCodeLauncher _launcher;

    public ClaudeCodeLauncherTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _launcher = new ClaudeCodeLauncher(_loggerFactory);
    }

    [Fact]
    public void Tool_IsClaudeCode()
    {
        _launcher.Tool.ShouldBe("claude-code");
    }

    [Fact]
    public async Task PrepareAsync_ReturnsWorkspaceFilesAndEnvVars_WithoutTouchingDisk()
    {
        var context = new AgentLaunchContext(
            AgentId: "ada",
            ConversationId: "conv-42",
            Prompt: "## Platform Instructions\nBe helpful.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "top-secret-token");

        // The launcher must not write to the local filesystem any more —
        // workspace materialisation lives in the dispatcher (issue #1042).
        // We snapshot the temp dir before the call so we can assert nothing
        // new appeared.
        var preExisting = new HashSet<string>(Directory.EnumerateFileSystemEntries(Path.GetTempPath()));

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        var postExisting = Directory.EnumerateFileSystemEntries(Path.GetTempPath());
        postExisting.Where(p => !preExisting.Contains(p))
            .ShouldBeEmpty("ClaudeCodeLauncher must not touch the local filesystem");

        prep.WorkspaceMountPath.ShouldBe("/workspace");
        prep.WorkspaceFiles.Keys.ShouldBe(new[] { "CLAUDE.md", ".mcp.json" }, ignoreOrder: true);
        prep.WorkspaceFiles["CLAUDE.md"].ShouldBe(context.Prompt);

        var parsed = JsonDocument.Parse(prep.WorkspaceFiles[".mcp.json"]).RootElement;
        var server = parsed.GetProperty("mcpServers").GetProperty("spring-voyage");
        server.GetProperty("url").GetString().ShouldBe(context.McpEndpoint);
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .ShouldBe("Bearer top-secret-token");

        prep.EnvironmentVariables["SPRING_AGENT_ID"].ShouldBe(context.AgentId);
        prep.EnvironmentVariables["SPRING_CONVERSATION_ID"].ShouldBe(context.ConversationId);
        prep.EnvironmentVariables["SPRING_MCP_ENDPOINT"].ShouldBe(context.McpEndpoint);
        prep.EnvironmentVariables["SPRING_AGENT_TOKEN"].ShouldBe(context.McpToken);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldBe(context.Prompt);

        prep.ExtraVolumeMounts.ShouldBeNull();
        prep.WorkingDirectory.ShouldBeNull(
            "leaving WorkingDirectory unset lets the dispatcher default to WorkspaceMountPath");
    }
}