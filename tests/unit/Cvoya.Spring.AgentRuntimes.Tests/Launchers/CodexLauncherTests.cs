// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Tests.Launchers;

using System.Text.Json;

using Cvoya.Spring.AgentRuntimes.Launchers;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.ModelProviders;

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
    private const string BundleContextMcpEndpoint = "http://host.docker.internal:9999/mcp/";

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
    public async Task PrepareAsync_ReturnsEnvVars()
    {
        // ADR-0055: the launcher emits env vars only; the workspace files
        // (AGENTS.md / .mcp.json) move to ContributeBundleAsync.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nWrite clean code.",
            mcpToken: "codex-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        // #1322: SPRING_AGENT_ID, SPRING_MCP_ENDPOINT, SPRING_AGENT_TOKEN removed —
        // AgentContextBuilder emits the D1-canonical names for all launchers.
        prep.EnvironmentVariables.ContainsKey("SPRING_AGENT_ID").ShouldBeFalse(
            "SPRING_AGENT_ID is now emitted by AgentContextBuilder, not the launcher");
        prep.EnvironmentVariables.ContainsKey("SPRING_MCP_ENDPOINT").ShouldBeFalse(
            "SPRING_MCP_ENDPOINT superseded by D1-canonical SPRING_MCP_URL (AgentContextBuilder)");
        prep.EnvironmentVariables.ContainsKey("SPRING_AGENT_TOKEN").ShouldBeFalse(
            "SPRING_AGENT_TOKEN superseded by D1-canonical SPRING_MCP_TOKEN (AgentContextBuilder)");
        prep.EnvironmentVariables["SPRING_THREAD_ID"].ShouldBe(context.ThreadId);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldEndWith(context.Prompt);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldContain("Spring Voyage runtime guard — response discipline");
        _callbackSupport.AssertCallbackEnvironment(prep, context);

        prep.ExtraVolumeMounts.ShouldBeNull();
        prep.WorkingDirectory.ShouldBeNull();
    }

    [Fact]
    public async Task ContributeBundleAsync_ReturnsAgentsMdAndMcpJsonFiles()
    {
        // ADR-0055 §3: launcher-owned in-workspace files live in the
        // bootstrap contribution, not on the launch spec.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(),
            TestContext.Current.CancellationToken);

        contribution.Files.Keys.ShouldBe(new[] { "AGENTS.md", ".mcp.json" }, ignoreOrder: true);
        contribution.Files["AGENTS.md"].ShouldBe("Write clean code.");

        var parsed = JsonDocument.Parse(contribution.Files[".mcp.json"]).RootElement;
        var server = parsed.GetProperty("mcpServers").GetProperty("spring-voyage");
        server.GetProperty("url").GetString().ShouldBe(BundleContextMcpEndpoint);
        // ADR-0052 §4 / ADR-0055: empty Authorization placeholder.
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .ShouldBe("Bearer ");

        contribution.PlatformFilePaths.ShouldBe(new[] { "AGENTS.md", ".mcp.json" }, ignoreOrder: true);
    }

    [Fact]
    public async Task PrepareAsync_SetsSpringWorkspacePath_ToPerMemberMountPath()
    {
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "Be helpful.",
            mcpToken: "codex-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey(AgentWorkspaceContract.WorkspacePathEnvVar);
        prep.EnvironmentVariables[AgentWorkspaceContract.WorkspacePathEnvVar]
            .ShouldBe(AgentWorkspaceContract.BuildMountPath(context.AgentId));
    }

    [Fact]
    public async Task ContributeBundleAsync_WritesOnlyTheSinglePlatformMcpServer()
    {
        // ADR-0051: one MCP server serves every sv.* tool — sv.messaging.*
        // included. The bundle no longer writes a second messaging server.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(),
            TestContext.Current.CancellationToken);

        var mcpServers = GetMcpServers(contribution);
        mcpServers.EnumerateObject().Select(property => property.Name)
            .ShouldBe(new[] { "spring-voyage" });
    }

    [Fact]
    public async Task PrepareAsync_SetsSpringMcpConfigPath_UnderPerMemberMount()
    {
        // ADR-0052 §4: SPRING_MCP_CONFIG points the bridge at the .mcp.json
        // it rewrites per turn with the delivered MCP session token.
        var context = LauncherCallbackTestSupport.CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldNotContainKey("SPRING_ORCHESTRATION_MCP_CONFIG");
        prep.EnvironmentVariables["SPRING_MCP_CONFIG"].ShouldBe(
            $"{AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId)}/.mcp.json");
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
        // #2189: producer tags the (code, source) on ex.Data so the
        // AgentActor catch attributes precisely.
        ex.Data[SpringException.IssueCodeDataKey].ShouldBe("CredentialMissing");
        ex.Data[SpringException.IssueSourceDataKey].ShouldBe("credential");
    }

    [Fact]
    public async Task PrepareAsync_ConcurrentThreadsTrue_PrependsBothGuardsToSystemPromptEnv()
    {
        // #2096 / ADR-0041 + #2493: when concurrent_threads is on, the
        // SPRING_SYSTEM_PROMPT env value MUST start with the
        // ResponseDiscipline guard (always-on) and additionally carry the
        // ConcurrentThreadsGuard. The user's prompt body is preserved — the
        // guards compose, they do not replace.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nWrite clean code.",
            mcpToken: "codex-secret-token") with
        { ConcurrentThreads = true };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldStartWith("## Spring Voyage runtime guard — response discipline");
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldContain("concurrent_threads is on");
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldContain(context.Prompt);
    }

    [Fact]
    public async Task PrepareAsync_ConcurrentThreadsFalse_StillPrependsResponseDisciplineGuard()
    {
        // Issue #2493: the ResponseDiscipline guard is universal — every
        // launched runtime sees it. The ConcurrentThreadsGuard remains
        // gated on the flag.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nWrite clean code.",
            mcpToken: "codex-secret-token") with
        { ConcurrentThreads = false };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldStartWith("## Spring Voyage runtime guard — response discipline");
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldEndWith(context.Prompt);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldNotContain("concurrent_threads is on");
    }

    private static AgentBootstrapContributionContext CreateBundleContext(
        string? instructions = "Write clean code.")
    {
        var definition = new AgentDefinition(
            AgentId: LauncherCallbackTestSupport.DefaultAgentAddress.Path,
            Name: "Test Agent",
            Instructions: instructions,
            Execution: new AgentExecutionConfig(
                Runtime: "codex",
                Image: "ghcr.io/test/codex:latest"));
        return new AgentBootstrapContributionContext(
            AgentId: LauncherCallbackTestSupport.DefaultAgentAddress.Path,
            Definition: definition,
            McpEndpoint: BundleContextMcpEndpoint);
    }

    private static JsonElement GetMcpServers(AgentBootstrapContribution contribution)
    {
        using var parsed = JsonDocument.Parse(contribution.Files[".mcp.json"]);
        return parsed.RootElement.GetProperty("mcpServers").Clone();
    }
}
