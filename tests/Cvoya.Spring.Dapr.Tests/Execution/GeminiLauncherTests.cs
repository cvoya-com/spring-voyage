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
    public async Task PrepareAsync_ReturnsWorkspaceFilesAndEnvVars()
    {
        // Note: an earlier revision also snapshot Path.GetTempPath() before
        // and after PrepareAsync to assert "doesn't touch the local
        // filesystem" (the launcher contract — see issue #1042). That
        // assertion races with any other parallel test (in any assembly)
        // that writes under /tmp, producing a recurring CI flake (#1082).
        // The contract is now enforced by code review on the launcher
        // implementation, which is pure-functional dictionary
        // construction.
        var context = new AgentLaunchContext(
            AgentId: "gemini-agent",
            ThreadId: "conv-88",
            Prompt: "## Platform Instructions\nAnalyze thoroughly.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "gemini-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.WorkspaceMountPath.ShouldBe("/workspace");
        prep.WorkspaceFiles.Keys.ShouldBe(new[] { "GEMINI.md", ".mcp.json" }, ignoreOrder: true);
        prep.WorkspaceFiles["GEMINI.md"].ShouldBe(context.Prompt);

        var parsed = JsonDocument.Parse(prep.WorkspaceFiles[".mcp.json"]).RootElement;
        var server = parsed.GetProperty("mcpServers").GetProperty("spring-voyage");
        server.GetProperty("url").GetString().ShouldBe(context.McpEndpoint);
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .ShouldBe("Bearer gemini-secret-token");

        // #1322: SPRING_AGENT_ID, SPRING_MCP_ENDPOINT, SPRING_AGENT_TOKEN removed —
        // AgentContextBuilder emits the D1-canonical names for all launchers.
        prep.EnvironmentVariables.ContainsKey("SPRING_AGENT_ID").ShouldBeFalse(
            "SPRING_AGENT_ID is now emitted by AgentContextBuilder, not the launcher");
        prep.EnvironmentVariables.ContainsKey("SPRING_MCP_ENDPOINT").ShouldBeFalse(
            "SPRING_MCP_ENDPOINT superseded by D1-canonical SPRING_MCP_URL (AgentContextBuilder)");
        prep.EnvironmentVariables.ContainsKey("SPRING_AGENT_TOKEN").ShouldBeFalse(
            "SPRING_AGENT_TOKEN superseded by D1-canonical SPRING_MCP_TOKEN (AgentContextBuilder)");
        prep.EnvironmentVariables["SPRING_THREAD_ID"].ShouldBe(context.ThreadId);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldBe(context.Prompt);

        prep.ExtraVolumeMounts.ShouldBeNull();
        prep.WorkingDirectory.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_SetsSpringWorkspacePath_ToCanonicalMountPath()
    {
        var context = new AgentLaunchContext(
            AgentId: "gemini-agent",
            ThreadId: "conv-1",
            Prompt: "Be helpful.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "gemini-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey(AgentVolumeManager.WorkspacePathEnvVar);
        prep.EnvironmentVariables[AgentVolumeManager.WorkspacePathEnvVar]
            .ShouldBe(AgentVolumeManager.WorkspaceMountPath);
    }
}