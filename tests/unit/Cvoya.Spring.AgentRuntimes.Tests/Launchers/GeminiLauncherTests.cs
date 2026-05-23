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
/// Unit tests for <see cref="GeminiLauncher"/>.
/// </summary>
public class GeminiLauncherTests
{
    private const string DefaultApiKey = "test-google-key";
    private const string BundleContextMcpEndpoint = "http://host.docker.internal:9999/mcp/";

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
    public async Task PrepareAsync_ReturnsEnvVars()
    {
        // ADR-0055: the launcher emits env vars only; the workspace files
        // (GEMINI.md / .gemini/settings.json) move to ContributeBundleAsync.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nAnalyze thoroughly.",
            mcpToken: "gemini-secret-token");

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
        // After the silent-dispatch cutover the response-discipline
        // contract lives in the platform-prompt layer. With
        // concurrent_threads off the launcher returns the prompt body
        // unchanged.
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldBe(context.Prompt);
        _callbackSupport.AssertCallbackEnvironment(prep, context);

        prep.ExtraVolumeMounts.ShouldBeNull();
        prep.WorkingDirectory.ShouldBeNull();
    }

    [Fact]
    public async Task ContributeBundleAsync_ReturnsGeminiMdAndSettingsFiles()
    {
        // ADR-0055 §3: launcher-owned in-workspace files live in the
        // bootstrap contribution, not on the launch spec.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(),
            TestContext.Current.CancellationToken);

        contribution.Files.Keys.ShouldBe(new[] { "GEMINI.md", ".gemini/settings.json" }, ignoreOrder: true);
        // GEMINI.md must equal the AssembledSystemPrompt handed in by
        // the bundle provider — NOT Definition.Instructions. After the
        // silent-dispatch cutover the assembler produces the full
        // multi-layer system prompt and the bundle writes that here.
        contribution.Files["GEMINI.md"].ShouldBe(TestAssembledSystemPrompt);

        using var settings = JsonDocument.Parse(contribution.Files[".gemini/settings.json"]);
        var server = settings.RootElement.GetProperty("mcpServers").GetProperty("spring-voyage");
        server.GetProperty("httpUrl").GetString().ShouldBe(BundleContextMcpEndpoint);
        // ADR-0052 §4 / ADR-0055: empty Authorization placeholder.
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .ShouldBe("Bearer ");

        contribution.PlatformFilePaths.ShouldBe(new[] { "GEMINI.md", ".gemini/settings.json" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ContributeBundleAsync_WritesOnlyTheSinglePlatformMcpServer()
    {
        // ADR-0051: one MCP server serves every sv.* tool — sv.messaging.*
        // included. The bundle no longer writes a second messaging server.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(),
            TestContext.Current.CancellationToken);

        using var settings = JsonDocument.Parse(contribution.Files[".gemini/settings.json"]);
        var servers = settings.RootElement.GetProperty("mcpServers");
        servers.EnumerateObject().Select(property => property.Name)
            .ShouldBe(new[] { "spring-voyage" });
    }

    [Fact]
    public async Task PrepareAsync_SetsSpringMcpConfigPath_UnderPerMemberMount()
    {
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nAnalyze thoroughly.",
            mcpToken: "gemini-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        // ADR-0052 §4: SPRING_MCP_CONFIG points the bridge at the Gemini
        // settings file it rewrites per turn with the delivered MCP
        // session token (same mcpServers.<name>.headers shape as .mcp.json).
        prep.EnvironmentVariables["SPRING_MCP_CONFIG"]
            .ShouldBe($"{AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId)}/.gemini/settings.json");
    }

    [Fact]
    public async Task PrepareAsync_SetsSpringWorkspacePath_ToPerMemberMountPath()
    {
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "Be helpful.",
            mcpToken: "gemini-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey(AgentWorkspaceContract.WorkspacePathEnvVar);
        prep.EnvironmentVariables[AgentWorkspaceContract.WorkspacePathEnvVar]
            .ShouldBe(AgentWorkspaceContract.BuildMountPath(context.AgentId));
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
        // #2189: producer tags the (code, source) on ex.Data.
        ex.Data[SpringException.IssueCodeDataKey].ShouldBe("CredentialMissing");
        ex.Data[SpringException.IssueSourceDataKey].ShouldBe("credential");
    }

    [Fact]
    public async Task PrepareAsync_ConcurrentThreadsTrue_PrependsConcurrentThreadsGuardToSystemPromptEnv()
    {
        // ADR-0041: when concurrent_threads is on, the launcher prepends
        // the ConcurrentThreadsGuard marker to the prompt body delivered
        // via SPRING_SYSTEM_PROMPT. The user's prompt body is preserved
        // as the tail. The universal response-discipline contract now
        // lives in the platform-prompt layer.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nAnalyze thoroughly.",
            mcpToken: "gemini-secret-token") with
        { ConcurrentThreads = true };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldStartWith("## Spring Voyage runtime guard — concurrent_threads is on");
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldContain(context.Prompt);
    }

    [Fact]
    public async Task PrepareAsync_ConcurrentThreadsFalse_LeavesPromptUnchanged()
    {
        // With concurrent_threads off the launcher returns the prompt
        // body unchanged — no guard prepended.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nAnalyze thoroughly.",
            mcpToken: "gemini-secret-token") with
        { ConcurrentThreads = false };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldBe(context.Prompt);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldNotContain("concurrent_threads is on");
    }

    [Fact]
    public async Task PrepareAsync_SetsThreadIdBindingEnvVars_ToGeminiSessionFlags()
    {
        // ADR-0041 / #2103: the launcher tells the bridge to bind the
        // platform thread.id onto Gemini's session identifier via
        // `--session-id <id>` on first send and `--resume <id>` on
        // subsequent sends. These two consts are the wire contract
        // between the .NET launcher and `src/Cvoya.Spring.AgentSidecar/src/config.ts`
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
    public async Task PrepareAsync_SetsSpringAgentArgv_AsJsonEncodedArrayOfStrings()
    {
        // #2108: until this issue the launcher left SPRING_AGENT_ARGV
        // unset and the bridge had nothing to append the per-message
        // session-id flag onto — Gemini agent containers literally could
        // not execute end-to-end. The default argv must be JSON-encoded
        // (the bridge does JSON.parse, see
        // `src/Cvoya.Spring.AgentSidecar/src/config.ts:parseArgv`) and must
        // produce a complete spawn vector once the bridge appends
        // [--session-id|--resume, <thread.id>] to it.
        //
        // Each flag below has a documented rationale — see the doc-comment
        // on `GeminiLauncher.DefaultGeminiArgv`. The values are pinned
        // here so accidental drift surfaces in unit-test failure rather
        // than at integration time. In particular, the empty-string
        // sentinel after `--prompt` is what the upstream `gemini --help`
        // documents as the headless-mode trigger ("Appended to input on
        // stdin (if any)"); without it the CLI defaults to interactive
        // mode and never exits, hanging the bridge.
        var prep = await _launcher.PrepareAsync(
            LauncherCallbackTestSupport.CreateContext(
                prompt: "Be helpful.",
                mcpToken: "gemini-secret-token"),
            TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey("SPRING_AGENT_ARGV");
        var raw = prep.EnvironmentVariables["SPRING_AGENT_ARGV"];

        var argv = JsonSerializer.Deserialize<string[]>(raw);
        argv.ShouldNotBeNull();
        argv.ShouldBe(new[]
        {
            "gemini",
            "--prompt",
            string.Empty,
            "--output-format",
            "stream-json",
            "--yolo",
            "--skip-trust",
        });
    }

    [Fact]
    public async Task PrepareAsync_PointsGeminiCliHome_AtPerMemberWorkspaceMount()
    {
        // ADR-0041 / #2103: GEMINI_CLI_HOME relocates Gemini's config and
        // session-storage root. gemini-cli appends `.gemini/` to it and
        // writes chat checkpoints at
        // `<home>/.gemini/tmp/<project-hash>/chats/<sid>.json`. Anchoring
        // it on the per-agent workspace volume is what makes session files
        // survive container restart so the next `--resume <sid>` finds them.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "Be helpful.",
            mcpToken: "gemini-secret-token");
        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["GEMINI_CLI_HOME"]
            .ShouldBe(AgentWorkspaceContract.BuildMountPath(context.AgentId));
    }

    private const string TestAssembledSystemPrompt = "ASSEMBLED SYSTEM PROMPT FOR TEST";

    private static AgentBootstrapContributionContext CreateBundleContext(
        string? instructions = "Analyze thoroughly.",
        string assembledSystemPrompt = TestAssembledSystemPrompt)
    {
        var definition = new AgentDefinition(
            AgentId: LauncherCallbackTestSupport.DefaultAgentAddress.Path,
            Name: "Test Agent",
            Instructions: instructions,
            Execution: new AgentExecutionConfig(
                Runtime: "gemini",
                Image: "ghcr.io/test/gemini:latest"));
        return new AgentBootstrapContributionContext(
            AgentId: LauncherCallbackTestSupport.DefaultAgentAddress.Path,
            Definition: definition,
            McpEndpoint: BundleContextMcpEndpoint,
            AssembledSystemPrompt: assembledSystemPrompt);
    }
}
