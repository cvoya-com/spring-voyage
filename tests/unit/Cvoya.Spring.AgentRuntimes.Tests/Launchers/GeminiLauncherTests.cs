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
        // #2668: the Gemini CLI never reads SPRING_SYSTEM_PROMPT — the
        // system prompt is delivered via GEMINI.md from
        // ContributeBundleAsync.
        prep.EnvironmentVariables.ContainsKey("SPRING_SYSTEM_PROMPT").ShouldBeFalse(
            "Gemini consumes its system prompt from GEMINI.md (the bundle path), not SPRING_SYSTEM_PROMPT");
        _callbackSupport.AssertCallbackEnvironment(prep, context);

        prep.ExtraVolumeMounts.ShouldBeNull();
        prep.WorkingDirectory.ShouldBeNull();
    }

    [Fact]
    public async Task ContributeBundleAsync_AppendMode_WritesGeminiMd()
    {
        // ADR-0055 §3: launcher-owned in-workspace files live in the
        // bootstrap contribution, not on the launch spec. ADR-0057 §1:
        // the `.gemini/settings.json` MCP server entry is `command`-typed
        // (stdio), not `httpUrl`-typed — the CLI spawns the sidecar
        // binary in MCP-server mode per turn rather than dialling the
        // worker across the network.
        //
        // #2695: Append mode (default) → write to GEMINI.md, the only
        // Append-mode delivery channel Gemini supports (no append flag
        // in gemini-cli 0.41.x).
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(),
            TestContext.Current.CancellationToken);

        contribution.Files.Keys.ShouldBe(new[] { "GEMINI.md", ".gemini/settings.json" }, ignoreOrder: true);
        contribution.Files["GEMINI.md"].ShouldBe(TestAssembledSystemPrompt);
        contribution.Files.ShouldNotContainKey(".spring/system-prompt.md",
            "Append mode delivers via auto-discovered GEMINI.md, not the .spring/ namespace (#2695)");

        using var settings = JsonDocument.Parse(contribution.Files[".gemini/settings.json"]);
        var server = settings.RootElement.GetProperty("mcpServers").GetProperty("spring-voyage");

        // ADR-0057 §1: stdio MCP server.
        server.GetProperty("command").GetString().ShouldBe("node");
        var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        args.ShouldBe(new[] { "/opt/spring-voyage/sidecar/dist/cli.js", "mcp" });

        var env = server.GetProperty("env");
        env.GetProperty("SPRING_MCP_PROXY_URL").GetString().ShouldBe(BundleContextMcpEndpoint);
        env.GetProperty("SPRING_WORKSPACE_PATH").GetString()
            .ShouldBe(AgentWorkspaceContract.BuildMountPath(
                LauncherCallbackTestSupport.DefaultAgentAddress.Path));

        // ADR-0057 §3: no Authorization header / remote URL.
        server.TryGetProperty("headers", out _).ShouldBeFalse();
        server.TryGetProperty("httpUrl", out _).ShouldBeFalse();

        contribution.PlatformFilePaths.ShouldBe(new[] { "GEMINI.md", ".gemini/settings.json" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ContributeBundleAsync_ReplaceMode_WritesPlatformPromptFile()
    {
        // #2695: Replace mode → write to `.spring/system-prompt.md`
        // under ADR-0058 §2.2.2's namespace. PrepareAsync sets
        // GEMINI_SYSTEM_MD to the absolute path so the CLI drops its
        // own baseline. GEMINI.md is NOT written in this mode.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(systemPromptMode: Cvoya.Spring.Core.Catalog.SystemPromptMode.Replace),
            TestContext.Current.CancellationToken);

        contribution.Files.Keys.ShouldBe(
            new[] { ".spring/system-prompt.md", ".gemini/settings.json" }, ignoreOrder: true);
        contribution.Files[".spring/system-prompt.md"].ShouldBe(TestAssembledSystemPrompt);
        contribution.Files.ShouldNotContainKey("GEMINI.md",
            "Replace mode drops auto-discovery; GEMINI_SYSTEM_MD names the platform file directly (#2695)");

        contribution.PlatformFilePaths.ShouldBe(
            new[] { ".spring/system-prompt.md", ".gemini/settings.json" }, ignoreOrder: true);
    }

    [Fact]
    public async Task PrepareAsync_AppendMode_LeavesGeminiSystemMdUnset()
    {
        // #2695: Gemini has no append flag — Append mode's only
        // delivery channel is auto-discovered GEMINI.md. The launcher
        // must NOT set GEMINI_SYSTEM_MD, which would silently force
        // Replace semantics regardless of the agent's declared mode.
        var context = LauncherCallbackTestSupport.CreateContext(
            systemPromptMode: Cvoya.Spring.Core.Catalog.SystemPromptMode.Append);

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldNotContainKey("GEMINI_SYSTEM_MD");
    }

    [Fact]
    public async Task PrepareAsync_ReplaceMode_SetsGeminiSystemMdToPlatformPromptFile()
    {
        // #2695: Replace mode points GEMINI_SYSTEM_MD at the absolute
        // path of `.spring/system-prompt.md` on the per-member
        // workspace mount; the CLI's coding-assistant baseline is
        // dropped entirely.
        var context = LauncherCallbackTestSupport.CreateContext(
            systemPromptMode: Cvoya.Spring.Core.Catalog.SystemPromptMode.Replace);

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        var workspaceNoSlash = AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId);
        prep.EnvironmentVariables["GEMINI_SYSTEM_MD"]
            .ShouldBe($"{workspaceNoSlash}/.spring/system-prompt.md");
    }

    [Fact]
    public async Task PrepareAsync_DefaultMode_LeavesGeminiSystemMdUnset()
    {
        // #2695: default → Append (per the dispatcher's fallback); no
        // GEMINI_SYSTEM_MD override.
        var context = LauncherCallbackTestSupport.CreateContext();
        context.SystemPromptMode.ShouldBe(Cvoya.Spring.Core.Catalog.SystemPromptMode.Append);

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldNotContainKey("GEMINI_SYSTEM_MD");
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
    public async Task PrepareAsync_DoesNotSetSpringMcpConfigEnvVar()
    {
        // ADR-0057 §3: the launcher no longer points the sidecar at the
        // Gemini settings file via SPRING_MCP_CONFIG — the per-turn
        // token rides through the workspace-resident token store, not
        // the CLI's MCP config file.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "## Platform Instructions\nAnalyze thoroughly.",
            mcpToken: "gemini-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldNotContainKey("SPRING_MCP_CONFIG");
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

    // #2668 / #2738: the launcher-level ConcurrentThreadsGuard tests
    // moved with the guard fold. The Gemini CLI never reads
    // SPRING_SYSTEM_PROMPT — the guard now travels via GEMINI.md, which
    // AgentBootstrapBundleProvider composes by threading
    // `ConcurrentThreadsGuard: true` through PromptAssemblyContext
    // (#2738 — was a post-assemble LauncherPromptFragments.Compose call
    // pre-cutover) before handing the assembled body to
    // ContributeBundleAsync. The guard's delivery is therefore covered
    // by AgentBootstrapBundleProviderTests and the in-band render shape
    // by PromptAssemblerTests.

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

    /// <summary>
    /// #2682: the launcher contributes runtime-true workspace prose
    /// naming the Gemini CLI surface and workspace env vars.
    /// </summary>
    [Fact]
    public void GetWorkspacePromptFragment_NamesGeminiRuntimeAndWorkspaceEnvVars()
    {
        var fragment = _launcher.GetWorkspacePromptFragment();

        fragment.ShouldNotBeNullOrWhiteSpace();
        fragment!.ShouldContain("Gemini CLI");
        fragment.ShouldContain("`gemini`");
        fragment.ShouldContain("$SPRING_WORKSPACE_PATH");
        fragment.ShouldContain("GEMINI.md");
        fragment.ShouldContain(".gemini/settings.json");
        fragment.ShouldContain("$GEMINI_CLI_HOME");
        // #2695: the fragment covers the Replace-mode override env var
        // so authors know the per-mode delivery channel.
        fragment.ShouldContain("$GEMINI_SYSTEM_MD");
        fragment.ShouldNotContain("worktree");
    }

    private static AgentBootstrapContributionContext CreateBundleContext(
        string? instructions = "Analyze thoroughly.",
        string assembledSystemPrompt = TestAssembledSystemPrompt,
        Cvoya.Spring.Core.Catalog.SystemPromptMode? systemPromptMode = null)
    {
        var definition = new AgentDefinition(
            AgentId: LauncherCallbackTestSupport.DefaultAgentAddress.Path,
            Name: "Test Agent",
            Instructions: instructions,
            Execution: new AgentExecutionConfig(
                Runtime: "gemini",
                Image: "ghcr.io/test/gemini:latest",
                SystemPromptMode: systemPromptMode));
        return new AgentBootstrapContributionContext(
            AgentId: LauncherCallbackTestSupport.DefaultAgentAddress.Path,
            Definition: definition,
            McpEndpoint: BundleContextMcpEndpoint,
            AssembledSystemPrompt: assembledSystemPrompt);
    }
}
