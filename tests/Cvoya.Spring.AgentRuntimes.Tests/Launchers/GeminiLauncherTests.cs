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
/// Unit tests for <see cref="GeminiLauncher"/>.
/// </summary>
public class GeminiLauncherTests
{
    private const string DefaultApiKey = "test-google-key";

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILlmCredentialResolver _credentialResolver;
    private readonly LauncherCallbackTestSupport _callbackSupport;
    private readonly GeminiLauncher _launcher;

    public GeminiLauncherTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _credentialResolver = Substitute.For<ILlmCredentialResolver>();
        _credentialResolver
            .ResolveAsync("google", Cvoya.Spring.Core.Catalog.AuthMethod.ApiKey, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: DefaultApiKey,
                Source: LlmCredentialSource.Tenant,
                SecretName: "google-api-key"));

        _callbackSupport = new LauncherCallbackTestSupport();
        var scopeFactory = TestScopeFactory.For(_credentialResolver);
        _launcher = new GeminiLauncher(scopeFactory, _loggerFactory, _callbackSupport.Builder);
    }

    [Fact]
    public void Kind_IsGeminiCli()
    {
        // #1732: gemini-cli is the canonical launcher id for a future
        // Gemini catalogue runtime entry.
        _launcher.Kind.ShouldBe("gemini-cli");
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
            prompt: "## Platform Instructions\nAnalyze thoroughly.",
            mcpToken: "gemini-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.WorkspaceMountPath.ShouldBe("/workspace");
        prep.WorkspaceFiles.Keys.ShouldBe(new[] { "GEMINI.md", ".gemini/settings.json" }, ignoreOrder: true);
        prep.WorkspaceFiles["GEMINI.md"].ShouldBe(context.Prompt);

        using var settings = ParseGeminiSettings(prep);
        var server = settings.RootElement.GetProperty("mcpServers").GetProperty("spring-voyage");
        server.GetProperty("httpUrl").GetString().ShouldBe(context.McpEndpoint);
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
        _callbackSupport.AssertCallbackEnvironment(prep, context);

        prep.ExtraVolumeMounts.ShouldBeNull();
        prep.WorkingDirectory.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_OrchestrationToolsNullOrEmpty_DoesNotWriteOrchestrationMcpServer(
        bool useEmptyArray)
    {
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nAnalyze thoroughly.",
            mcpToken: "gemini-secret-token")
            with
        {
            OrchestrationTools = useEmptyArray ? Array.Empty<OrchestrationToolDescriptor>() : null
        };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        using var settings = ParseGeminiSettings(prep);
        var servers = settings.RootElement.GetProperty("mcpServers");
        servers.TryGetProperty("spring-orchestration", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task PrepareAsync_OrchestrationToolsPresent_WritesOrchestrationMcpServer()
    {
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nAnalyze thoroughly.",
            mcpToken: "gemini-secret-token")
            with
        {
            OrchestrationTools = CreateOrchestrationTools()
        };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        using var settings = ParseGeminiSettings(prep);
        var server = settings.RootElement
            .GetProperty("mcpServers")
            .GetProperty("spring-orchestration");

        server.GetProperty("httpUrl").GetString()
            .ShouldBe(LauncherCallbackTestSupport.OrchestrationMcpUrl);
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .ShouldBe($"Bearer {prep.EnvironmentVariables[AgentCallbackEnvironmentContract.CallbackTokenEnvVar]}");

        server.GetProperty("includeTools").EnumerateArray().Select(tool => tool.GetString()).ShouldBe(new[]
        {
            "list_children",
            "inspect_child",
            "delegate_to_child",
            "fanout_to_children",
            "query_child_status",
        });
    }

    [Fact]
    public async Task PrepareAsync_SetsSpringWorkspacePath_ToCanonicalMountPath()
    {
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "Be helpful.",
            mcpToken: "gemini-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey(AgentWorkspaceContract.WorkspacePathEnvVar);
        prep.EnvironmentVariables[AgentWorkspaceContract.WorkspacePathEnvVar]
            .ShouldBe(AgentWorkspaceContract.WorkspaceMountPath);
    }

    [Fact]
    public async Task PrepareAsync_InjectsGoogleApiKey_FromCredentialResolver()
    {
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "Be helpful.",
            mcpToken: "gemini-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["GOOGLE_API_KEY"].ShouldBe(DefaultApiKey);
    }

    [Fact]
    public async Task PrepareAsync_MissingCredential_FailsPreFlightWithGuidance()
    {
        _credentialResolver
            .ResolveAsync("google", Cvoya.Spring.Core.Catalog.AuthMethod.ApiKey, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: null,
                Source: LlmCredentialSource.NotFound,
                SecretName: "google-api-key"));

        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "Be helpful.",
            mcpToken: "gemini-secret-token");

        var ex = await Should.ThrowAsync<SpringException>(
            () => _launcher.PrepareAsync(context, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("google-api-key");
        ex.Message.ShouldContain("agent, unit, parent-unit chain, or tenant scope");
    }

    [Fact]
    public async Task PrepareAsync_ConcurrentThreadsTrue_PrependsGuardToGEMINImd_AndSystemPromptEnv()
    {
        // #2096 / ADR-0041: when concurrent_threads is on, the assembled
        // prompt the model sees (GEMINI.md, SPRING_SYSTEM_PROMPT) MUST
        // start with the shared launcher guard. The user's prompt body
        // is preserved in full — the guard composes, it does not replace.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nAnalyze thoroughly.",
            mcpToken: "gemini-secret-token") with
        { ConcurrentThreads = true };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.WorkspaceFiles["GEMINI.md"].ShouldStartWith("## Spring Voyage runtime guard");
        prep.WorkspaceFiles["GEMINI.md"].ShouldContain(context.Prompt);
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
            prompt: "## Platform Instructions\nAnalyze thoroughly.",
            mcpToken: "gemini-secret-token") with
        { ConcurrentThreads = false };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.WorkspaceFiles["GEMINI.md"].ShouldBe(context.Prompt);
        prep.WorkspaceFiles["GEMINI.md"].ShouldNotContain("Spring Voyage runtime guard");
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldBe(context.Prompt);
    }

    [Fact]
    public async Task PrepareAsync_SetsThreadIdBindingEnvVars_ToGeminiSessionFlags()
    {
        // ADR-0041 / #2103: the launcher tells the bridge to bind the
        // platform thread.id onto Gemini's session identifier via
        // `--session-id <id>` on first send and `--resume <id>` on
        // subsequent sends. These two consts are the wire contract
        // between the .NET launcher and `deployment/agent-sidecar/src/config.ts`
        // — pinning them here surfaces accidental drift in a unit test
        // rather than only at integration time.
        var prep = await _launcher.PrepareAsync(
            LauncherCallbackTestSupport.CreateContext(
                prompt: "Be helpful.",
                mcpToken: "gemini-secret-token"),
            TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_THREAD_ID_ARG_CREATE"].ShouldBe("--session-id");
        prep.EnvironmentVariables["SPRING_THREAD_ID_ARG_RESUME"].ShouldBe("--resume");
    }

    [Fact]
    public async Task PrepareAsync_PointsGeminiCliHome_AtWorkspaceMountPath()
    {
        // ADR-0041 / #2103: GEMINI_CLI_HOME relocates Gemini's config and
        // session-storage root. gemini-cli appends `.gemini/` to it and
        // writes chat checkpoints at
        // `<home>/.gemini/tmp/<project-hash>/chats/<sid>.json`. Anchoring
        // it on the per-agent workspace volume is what makes session files
        // survive container restart so the next `--resume <sid>` finds them.
        var prep = await _launcher.PrepareAsync(
            LauncherCallbackTestSupport.CreateContext(
                prompt: "Be helpful.",
                mcpToken: "gemini-secret-token"),
            TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["GEMINI_CLI_HOME"]
            .ShouldBe(AgentWorkspaceContract.WorkspaceMountPath);
    }

    private static JsonDocument ParseGeminiSettings(AgentLaunchSpec prep) =>
        JsonDocument.Parse(prep.WorkspaceFiles[".gemini/settings.json"]);

    private static OrchestrationToolDescriptor[] CreateOrchestrationTools()
    {
        var inputSchema = CreateSchema();
        var outputSchema = CreateSchema();
        return new[]
        {
            new OrchestrationToolDescriptor(OrchestrationToolName.ListChildren, inputSchema, outputSchema),
            new OrchestrationToolDescriptor(OrchestrationToolName.InspectChild, inputSchema, outputSchema),
            new OrchestrationToolDescriptor(OrchestrationToolName.DelegateToChild, inputSchema, outputSchema),
            new OrchestrationToolDescriptor(OrchestrationToolName.FanoutToChildren, inputSchema, outputSchema),
            new OrchestrationToolDescriptor(OrchestrationToolName.QueryChildStatus, inputSchema, outputSchema),
        };
    }

    private static JsonElement CreateSchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object"}""");
        return document.RootElement.Clone();
    }
}
