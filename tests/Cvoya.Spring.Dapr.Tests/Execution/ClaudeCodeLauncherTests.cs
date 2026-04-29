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
    public async Task PrepareAsync_ReturnsWorkspaceFilesAndEnvVars()
    {
        // The launcher must not write to the local filesystem — workspace
        // materialisation lives in the dispatcher (issue #1042). An earlier
        // revision snapshot Path.GetTempPath() before/after PrepareAsync
        // to assert that, but the assertion races with any parallel test
        // (in any assembly) that writes under /tmp, producing a recurring
        // CI flake (#1082). The contract is now enforced by code review
        // on the launcher implementation, which is pure-functional
        // dictionary construction.
        var context = new AgentLaunchContext(
            AgentId: "ada",
            ThreadId: "conv-42",
            Prompt: "## Platform Instructions\nBe helpful.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "top-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.WorkspaceMountPath.ShouldBe("/workspace");
        prep.WorkspaceFiles.Keys.ShouldBe(new[] { "CLAUDE.md", ".mcp.json" }, ignoreOrder: true);
        prep.WorkspaceFiles["CLAUDE.md"].ShouldBe(context.Prompt);

        var parsed = JsonDocument.Parse(prep.WorkspaceFiles[".mcp.json"]).RootElement;
        var server = parsed.GetProperty("mcpServers").GetProperty("spring-voyage");
        server.GetProperty("url").GetString().ShouldBe(context.McpEndpoint);
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .ShouldBe("Bearer top-secret-token");

        prep.EnvironmentVariables["SPRING_AGENT_ID"].ShouldBe(context.AgentId);
        prep.EnvironmentVariables["SPRING_THREAD_ID"].ShouldBe(context.ThreadId);
        prep.EnvironmentVariables["SPRING_MCP_ENDPOINT"].ShouldBe(context.McpEndpoint);
        prep.EnvironmentVariables["SPRING_AGENT_TOKEN"].ShouldBe(context.McpToken);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldBe(context.Prompt);

        prep.ExtraVolumeMounts.ShouldBeNull();
        prep.WorkingDirectory.ShouldBeNull(
            "leaving WorkingDirectory unset lets the dispatcher default to WorkspaceMountPath");
    }

    [Fact]
    public async Task PrepareAsync_LeavesArgvEmpty_SoAgentBaseBridgeOwnsTheEntrypoint()
    {
        // BYOI conformance path 1: an empty Argv tells the dispatcher to
        // honour the image's ENTRYPOINT — for agent-base, that is the
        // TypeScript A2A bridge which spawns the real CLI from
        // SPRING_AGENT_ARGV. See issue #1097.
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        prep.Argv.ShouldNotBeNull();
        prep.Argv.ShouldBeEmpty(
            "claude-code goes through the agent-base bridge — Argv must be empty so the bridge ENTRYPOINT wins");
    }

    [Fact]
    public async Task PrepareAsync_SetsSpringAgentArgv_AsJsonEncodedArrayOfStrings()
    {
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey("SPRING_AGENT_ARGV");
        var raw = prep.EnvironmentVariables["SPRING_AGENT_ARGV"];

        // The bridge does JSON.parse on this value (see
        // deployment/agent-sidecar/src/config.ts). Round-tripping it
        // through JsonSerializer is the contract.
        var argv = JsonSerializer.Deserialize<string[]>(raw);
        argv.ShouldNotBeNull();
        argv.ShouldBe(new[]
        {
            "claude",
            "--print",
            "--dangerously-skip-permissions",
            "--output-format",
            "stream-json",
        });
    }

    [Fact]
    public async Task PrepareAsync_SetsStdinPayload_ToTheAssembledPrompt()
    {
        var context = CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        // The bridge will pipe this on `claude`'s stdin (PR 5). It must
        // carry the same prompt body the launcher already exposes via
        // CLAUDE.md and SPRING_SYSTEM_PROMPT — no new format.
        prep.StdinPayload.ShouldBe(context.Prompt);
        prep.StdinPayload.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PrepareAsync_DefaultsA2APortAndResponseCapture()
    {
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        // Defaults are part of the wire contract — assert them so a
        // change is intentional and caught here rather than in PR 5.
        prep.A2APort.ShouldBe(8999);
        prep.ResponseCapture.ShouldBe(AgentResponseCapture.A2A);
    }

    private static AgentLaunchContext CreateContext() =>
        new(
            AgentId: "ada",
            ThreadId: "conv-42",
            Prompt: "## Platform Instructions\nBe helpful.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "top-secret-token");
}