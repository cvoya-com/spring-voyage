// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Tests.Launchers;

using System.Text.Json;

using Cvoya.Spring.AgentRuntimes.Launchers;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Orchestration;

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
    private readonly ILlmCredentialResolver _credentialResolver;
    private readonly LauncherCallbackTestSupport _callbackSupport;
    private readonly CodexLauncher _launcher;

    public CodexLauncherTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _credentialResolver = Substitute.For<ILlmCredentialResolver>();
        _credentialResolver
            .ResolveAsync("openai", Cvoya.Spring.Core.Catalog.AuthMethod.ApiKey, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: DefaultApiKey,
                Source: LlmCredentialSource.Tenant,
                SecretName: "openai-api-key"));

        _callbackSupport = new LauncherCallbackTestSupport();
        var scopeFactory = TestScopeFactory.For(_credentialResolver);
        _launcher = new CodexLauncher(scopeFactory, _loggerFactory, _callbackSupport.Builder);
    }

    [Fact]
    public void Kind_IsCodexCli()
    {
        // #1732: codex-cli is the canonical launcher id for the Codex
        // catalogue runtime entry.
        _launcher.Kind.ShouldBe("codex-cli");
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
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nWrite clean code.",
            mcpToken: "codex-secret-token");

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
        _callbackSupport.AssertCallbackEnvironment(prep, context);

        prep.ExtraVolumeMounts.ShouldBeNull();
        prep.WorkingDirectory.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_SetsSpringWorkspacePath_ToCanonicalMountPath()
    {
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "Be helpful.",
            mcpToken: "codex-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey(AgentWorkspaceContract.WorkspacePathEnvVar);
        prep.EnvironmentVariables[AgentWorkspaceContract.WorkspacePathEnvVar]
            .ShouldBe(AgentWorkspaceContract.WorkspaceMountPath);
    }

    [Fact]
    public async Task PrepareAsync_OrchestrationToolsNullOrEmpty_DoesNotAddSpringOrchestrationMcpServer()
    {
        var nullToolsContext = LauncherCallbackTestSupport.CreateContext();
        var emptyToolsContext = nullToolsContext with
        {
            OrchestrationTools = Array.Empty<OrchestrationToolDescriptor>()
        };

        var nullToolsPrep = await _launcher.PrepareAsync(
            nullToolsContext, TestContext.Current.CancellationToken);
        var emptyToolsPrep = await _launcher.PrepareAsync(
            emptyToolsContext, TestContext.Current.CancellationToken);

        ShouldNotContainSpringOrchestrationServer(nullToolsPrep);
        ShouldNotContainSpringOrchestrationServer(emptyToolsPrep);
    }

    [Fact]
    public async Task PrepareAsync_OrchestrationToolsPresent_AddsSpringOrchestrationMcpServer()
    {
        var context = LauncherCallbackTestSupport.CreateContext() with
        {
            OrchestrationTools = CreateOrchestrationTools()
        };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        var mcpServers = GetMcpServers(prep);
        mcpServers.TryGetProperty("spring-orchestration", out var server).ShouldBeTrue();
        server.GetProperty("type").GetString().ShouldBe("http");
        server.GetProperty("url").GetString()
            .ShouldBe(LauncherCallbackTestSupport.OrchestrationMcpUrl);
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .ShouldBe(
                $"Bearer {prep.EnvironmentVariables[AgentCallbackEnvironmentContract.CallbackTokenEnvVar]}");
    }

    [Fact]
    public async Task PrepareAsync_InjectsOpenAiApiKey_FromCredentialResolver()
    {
        // #1714 step 2: Codex resolves the OpenAI API key through the
        // credential resolver and injects it as OPENAI_API_KEY.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "Be helpful.",
            mcpToken: "codex-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["OPENAI_API_KEY"].ShouldBe(DefaultApiKey);
    }

    [Fact]
    public async Task PrepareAsync_MissingCredential_FailsPreFlightWithGuidance()
    {
        _credentialResolver
            .ResolveAsync("openai", Cvoya.Spring.Core.Catalog.AuthMethod.ApiKey, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: null,
                Source: LlmCredentialSource.NotFound,
                SecretName: "openai-api-key"));

        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "Be helpful.",
            mcpToken: "codex-secret-token");

        var ex = await Should.ThrowAsync<SpringException>(
            () => _launcher.PrepareAsync(context, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("openai-api-key");
        ex.Message.ShouldContain("agent, unit, parent-unit chain, or tenant scope");
    }

    [Fact]
    public async Task PrepareAsync_ConcurrentThreadsTrue_PrependsGuardToAGENTSmd_AndSystemPromptEnv()
    {
        // #2096 / ADR-0041: when concurrent_threads is on, the assembled
        // prompt the model sees (AGENTS.md, SPRING_SYSTEM_PROMPT) MUST
        // start with the shared launcher guard. The user's prompt body
        // is preserved in full — the guard composes, it does not replace.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nWrite clean code.",
            mcpToken: "codex-secret-token") with
        { ConcurrentThreads = true };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.WorkspaceFiles["AGENTS.md"].ShouldStartWith("## Spring Voyage runtime guard");
        prep.WorkspaceFiles["AGENTS.md"].ShouldContain(context.Prompt);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldStartWith("## Spring Voyage runtime guard");
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldContain(context.Prompt);
    }

    [Fact]
    public async Task PrepareAsync_ConcurrentThreadsFalse_LeavesPromptVerbatim()
    {
        // The guard MUST NOT fire when the agent stays on the safe-default
        // mode — we don't want to bias every agent away from valid
        // patterns just because the launcher has a guard available.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nWrite clean code.",
            mcpToken: "codex-secret-token") with
        { ConcurrentThreads = false };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.WorkspaceFiles["AGENTS.md"].ShouldBe(context.Prompt);
        prep.WorkspaceFiles["AGENTS.md"].ShouldNotContain("Spring Voyage runtime guard");
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldBe(context.Prompt);
    }

    private static void ShouldNotContainSpringOrchestrationServer(AgentLaunchSpec prep)
    {
        var mcpServers = GetMcpServers(prep);

        mcpServers.TryGetProperty("spring-orchestration", out _).ShouldBeFalse();
    }

    private static JsonElement GetMcpServers(AgentLaunchSpec prep)
    {
        using var parsed = JsonDocument.Parse(prep.WorkspaceFiles[".mcp.json"]);

        return parsed.RootElement.GetProperty("mcpServers").Clone();
    }

    private static OrchestrationToolDescriptor[] CreateOrchestrationTools() =>
    [
        new(
            OrchestrationToolName.ListChildren,
            CreateObjectSchema(),
            CreateObjectSchema())
    ];

    private static JsonElement CreateObjectSchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object"}""");

        return document.RootElement.Clone();
    }
}
