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
/// Unit tests for <see cref="GeminiLauncher"/>.
/// </summary>
public class GeminiLauncherTests
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly GeminiLauncher _launcher;

    public GeminiLauncherTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _launcher = new GeminiLauncher(_loggerFactory);
    }

    [Fact]
    public void Tool_IsGemini()
    {
        _launcher.Tool.ShouldBe("gemini");
    }

    [Fact]
    public async Task PrepareAsync_ReturnsWorkspaceFilesAndEnvVars_WithoutTouchingDisk()
    {
        var context = new AgentLaunchContext(
            AgentId: "gemini-agent",
            ConversationId: "conv-88",
            Prompt: "## Platform Instructions\nAnalyze thoroughly.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "gemini-secret-token");

        var preExisting = new HashSet<string>(Directory.EnumerateFileSystemEntries(Path.GetTempPath()));

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        var postExisting = Directory.EnumerateFileSystemEntries(Path.GetTempPath());
        postExisting.Where(p => !preExisting.Contains(p))
            .ShouldBeEmpty("GeminiLauncher must not touch the local filesystem");

        prep.WorkspaceMountPath.ShouldBe("/workspace");
        prep.WorkspaceFiles.Keys.ShouldBe(new[] { "GEMINI.md", ".mcp.json" }, ignoreOrder: true);
        prep.WorkspaceFiles["GEMINI.md"].ShouldBe(context.Prompt);

        var parsed = JsonDocument.Parse(prep.WorkspaceFiles[".mcp.json"]).RootElement;
        var server = parsed.GetProperty("mcpServers").GetProperty("spring-voyage");
        server.GetProperty("url").GetString().ShouldBe(context.McpEndpoint);
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .ShouldBe("Bearer gemini-secret-token");

        prep.EnvironmentVariables["SPRING_AGENT_ID"].ShouldBe(context.AgentId);
        prep.EnvironmentVariables["SPRING_CONVERSATION_ID"].ShouldBe(context.ConversationId);
        prep.EnvironmentVariables["SPRING_MCP_ENDPOINT"].ShouldBe(context.McpEndpoint);
        prep.EnvironmentVariables["SPRING_AGENT_TOKEN"].ShouldBe(context.McpToken);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldBe(context.Prompt);

        prep.ExtraVolumeMounts.ShouldBeNull();
        prep.WorkingDirectory.ShouldBeNull();
    }
}