// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="CodexLauncher"/>.
/// </summary>
public class CodexLauncherTests
{
    private const string DefaultApiKey = "sk-test-codex-key";

    private readonly ILoggerFactory _loggerFactory;
    private readonly IAgentRuntimeRegistry _registry;
    private readonly ILlmCredentialResolver _credentialResolver;
    private readonly IAgentRuntime _openAiRuntime;
    private readonly CodexLauncher _launcher;

    public CodexLauncherTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _openAiRuntime = Substitute.For<IAgentRuntime>();
        _openAiRuntime.Id.Returns("openai");
        _openAiRuntime.CredentialSecretName.Returns("openai-api-key");
        _openAiRuntime.CredentialEnvVar.Returns("OPENAI_API_KEY");
        _openAiRuntime
            .IsCredentialFormatAccepted(Arg.Any<string>(), Arg.Any<CredentialDispatchPath>())
            .Returns(true);

        _registry = Substitute.For<IAgentRuntimeRegistry>();
        _registry.Get("openai").Returns(_openAiRuntime);

        _credentialResolver = Substitute.For<ILlmCredentialResolver>();
        _credentialResolver
            .ResolveAsync("openai", Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: DefaultApiKey,
                Source: LlmCredentialSource.Tenant,
                SecretName: "openai-api-key"));

        var scopeFactory = TestScopeFactory.For(_credentialResolver);
        _launcher = new CodexLauncher(_registry, scopeFactory, _loggerFactory);
    }

    [Fact]
    public void ToolKind_IsCodexCli()
    {
        // #1732: codex-cli is the canonical tool kind for a future Codex
        // runtime. No IAgentRuntime currently resolves to this launcher.
        _launcher.ToolKind.ShouldBe("codex-cli");
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
            AgentId: "codex-agent",
            ThreadId: "conv-77",
            Prompt: "## Platform Instructions\nWrite clean code.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "codex-secret-token",
            TenantId: Cvoya.Spring.Core.Tenancy.OssTenantIds.Default);

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.WorkspaceMountPath.ShouldBe("/workspace");
        prep.WorkspaceFiles.Keys.ShouldBe(new[] { "AGENTS.md", ".mcp.json" }, ignoreOrder: true);
        prep.WorkspaceFiles["AGENTS.md"].ShouldBe(context.Prompt);

        var parsed = JsonDocument.Parse(prep.WorkspaceFiles[".mcp.json"]).RootElement;
        var server = parsed.GetProperty("mcpServers").GetProperty("spring-voyage");
        server.GetProperty("url").GetString().ShouldBe(context.McpEndpoint);
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .ShouldBe("Bearer codex-secret-token");

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
            AgentId: "codex-agent",
            ThreadId: "conv-1",
            Prompt: "Be helpful.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "codex-secret-token",
            TenantId: Cvoya.Spring.Core.Tenancy.OssTenantIds.Default);

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey(AgentVolumeManager.WorkspacePathEnvVar);
        prep.EnvironmentVariables[AgentVolumeManager.WorkspacePathEnvVar]
            .ShouldBe(AgentVolumeManager.WorkspaceMountPath);
    }

    [Fact]
    public async Task PrepareAsync_InjectsOpenAiApiKey_FromCredentialResolver()
    {
        // #1714 step 2: Codex resolves the OpenAI API key through the
        // credential resolver and injects it as OPENAI_API_KEY.
        var context = new AgentLaunchContext(
            AgentId: "codex-agent",
            ThreadId: "conv-1",
            Prompt: "Be helpful.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "codex-secret-token",
            TenantId: Cvoya.Spring.Core.Tenancy.OssTenantIds.Default);

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["OPENAI_API_KEY"].ShouldBe(DefaultApiKey);
    }

    [Fact]
    public async Task PrepareAsync_MissingCredential_FailsPreFlightWithGuidance()
    {
        _credentialResolver
            .ResolveAsync("openai", Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: null,
                Source: LlmCredentialSource.NotFound,
                SecretName: "openai-api-key"));

        var context = new AgentLaunchContext(
            AgentId: "codex-agent",
            ThreadId: "conv-1",
            Prompt: "Be helpful.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "codex-secret-token",
            TenantId: Cvoya.Spring.Core.Tenancy.OssTenantIds.Default);

        var ex = await Should.ThrowAsync<SpringException>(
            () => _launcher.PrepareAsync(context, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("openai-api-key");
        ex.Message.ShouldContain("agent, unit, parent-unit chain, or tenant scope");
    }
}