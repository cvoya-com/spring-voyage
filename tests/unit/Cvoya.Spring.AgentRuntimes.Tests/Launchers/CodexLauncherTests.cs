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
        // #2668: the Codex CLI never reads SPRING_SYSTEM_PROMPT — the
        // system prompt is delivered via AGENTS.md from
        // ContributeBundleAsync.
        prep.EnvironmentVariables.ContainsKey("SPRING_SYSTEM_PROMPT").ShouldBeFalse(
            "Codex consumes its system prompt from AGENTS.md (the bundle path), not SPRING_SYSTEM_PROMPT");
        _callbackSupport.AssertCallbackEnvironment(prep, context);

        prep.ExtraVolumeMounts.ShouldBeNull();
        prep.WorkingDirectory.ShouldBeNull();
    }

    [Fact]
    public async Task ContributeBundleAsync_ReturnsAgentsMdAndMcpJsonFiles()
    {
        // ADR-0055 §3: launcher-owned in-workspace files live in the
        // bootstrap contribution, not on the launch spec. ADR-0057 §1:
        // the `.mcp.json` MCP server entry is `command`-typed (stdio),
        // not `http`-typed — the CLI spawns the sidecar binary in
        // MCP-server mode per turn rather than dialling the worker
        // across the network.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(),
            TestContext.Current.CancellationToken);

        contribution.Files.Keys.ShouldBe(new[] { "AGENTS.md", ".mcp.json" }, ignoreOrder: true);
        contribution.Files["AGENTS.md"].ShouldBe(TestAssembledSystemPrompt);

        var parsed = JsonDocument.Parse(contribution.Files[".mcp.json"]).RootElement;
        var server = parsed.GetProperty("mcpServers").GetProperty("spring-voyage");

        server.GetProperty("command").GetString().ShouldBe("node");
        var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        args.ShouldBe(new[] { "/opt/spring-voyage/sidecar/dist/cli.js", "mcp" });

        var env = server.GetProperty("env");
        env.GetProperty("SPRING_MCP_PROXY_URL").GetString().ShouldBe(BundleContextMcpEndpoint);
        env.GetProperty("SPRING_WORKSPACE_PATH").GetString()
            .ShouldBe(AgentWorkspaceContract.BuildMountPath(
                LauncherCallbackTestSupport.DefaultAgentAddress.Path));

        // ADR-0057 §3: no Authorization header / remote URL — the CLI
        // never sees the per-turn token; only the spawned
        // MCP-server-mode child holds it.
        server.TryGetProperty("headers", out _).ShouldBeFalse();
        server.TryGetProperty("url", out _).ShouldBeFalse();

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
    public async Task PrepareAsync_DoesNotSetSpringMcpConfigEnvVar()
    {
        // ADR-0057 §3: the `.mcp.json` Authorization-header rewrite path
        // is gone, so the launcher no longer needs to point the sidecar
        // at the file via SPRING_MCP_CONFIG — the per-turn token now
        // rides through the workspace-resident token store, not the
        // CLI's MCP config file.
        var context = LauncherCallbackTestSupport.CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldNotContainKey("SPRING_ORCHESTRATION_MCP_CONFIG");
        prep.EnvironmentVariables.ShouldNotContainKey("SPRING_MCP_CONFIG");
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

    // #2668 / #2738: the launcher-level ConcurrentConversationsGuard tests
    // moved with the guard fold. The Codex CLI never reads
    // SPRING_SYSTEM_PROMPT — the guard now travels via AGENTS.md, which
    // AgentBootstrapBundleProvider composes by threading
    // `ConcurrentConversationsGuard: true` through PromptAssemblyContext
    // (#2738 — was a post-assemble LauncherPromptFragments.Compose call
    // pre-cutover) before handing the assembled body to
    // ContributeBundleAsync. The guard's delivery is therefore covered
    // by AgentBootstrapBundleProviderTests and the in-band render shape
    // by PromptAssemblerTests.

    [Fact]
    public async Task ContributeBundleAsync_StillWritesAgentsMd_RegardlessOfSystemPromptMode()
    {
        // #2672 / #2695: Codex CLI has no `--system-prompt-*` flags
        // and no replace-only env var (openai/codex#11588). The
        // launcher always writes to the CLI's auto-discovered
        // `AGENTS.md` — Replace mode is honoured-by-best-effort only
        // (logged informationally; see PrepareAsync_ReplaceMode test).
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(),
            TestContext.Current.CancellationToken);

        contribution.Files.ShouldContainKey("AGENTS.md",
            "Codex's only system-prompt delivery channel is auto-discovered AGENTS.md " +
            "until openai/codex#11588 lands per-runtime override flags");
        contribution.Files.ShouldNotContainKey(".spring/system-prompt.md",
            "the .spring/ namespace move requires a CLI flag that Codex does not " +
            "currently expose (openai/codex#11588)");
    }

    [Fact]
    public async Task PrepareAsync_ReplaceMode_LogsInfoButOtherwiseUnchanged()
    {
        // #2695: Codex honours neither system_prompt_mode. When an
        // agent declares Replace on a Codex runtime, the launcher logs
        // an informational message so operators see the mismatch, then
        // proceeds with the default AGENTS.md delivery channel. Logging
        // is asserted by hooking the logger; here we sanity-check that
        // the env contract is unchanged.
        var context = LauncherCallbackTestSupport.CreateContext(
            systemPromptMode: Cvoya.Spring.Core.Catalog.SystemPromptMode.Replace);

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        // No Codex equivalent of GEMINI_SYSTEM_MD or
        // --system-prompt-file exists; nothing about Replace mode
        // changes the env block produced.
        prep.EnvironmentVariables.ShouldNotContainKey("GEMINI_SYSTEM_MD");
        prep.EnvironmentVariables.ShouldNotContainKey("CODEX_SYSTEM_PROMPT");
        prep.EnvironmentVariables.ShouldContainKey(AgentWorkspaceContract.WorkspacePathEnvVar);
    }

    private const string TestAssembledSystemPrompt = "ASSEMBLED SYSTEM PROMPT FOR TEST";

    /// <summary>
    /// #2682: the launcher contributes runtime-true workspace prose
    /// naming the Codex CLI surface and workspace env vars.
    /// </summary>
    [Fact]
    public void GetWorkspacePromptFragment_NamesCodexRuntimeAndWorkspaceEnvVars()
    {
        var fragment = _launcher.GetWorkspacePromptFragment();

        fragment.ShouldNotBeNullOrWhiteSpace();
        fragment!.ShouldContain("Codex CLI");
        fragment.ShouldContain("`codex`");
        fragment.ShouldContain("$SPRING_WORKSPACE_PATH");
        fragment.ShouldContain("AGENTS.md");
        fragment.ShouldContain(".mcp.json");
        fragment.ShouldNotContain("worktree");

        // #2742: the launcher fragment is CLI-universal, not image-
        // specific. Image-bundled tooling moves to per-image profiles
        // or role-specific instructions.
        fragment.ShouldNotContain("standard image bundles");
        fragment.ShouldNotContain("`dotnet`");
        fragment.ShouldNotContain("`gh`");
        fragment.ShouldNotContain("`git`");
        fragment.ShouldNotContain("`python3`");
        fragment.ShouldNotContain("Debian-based container");
        fragment.ShouldNotContain("Spring Voyage agent sidecar");
    }

    private static AgentBootstrapContributionContext CreateBundleContext(
        string? instructions = "Write clean code.",
        string assembledSystemPrompt = TestAssembledSystemPrompt)
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
            McpEndpoint: BundleContextMcpEndpoint,
            AssembledSystemPrompt: assembledSystemPrompt);
    }

    private static JsonElement GetMcpServers(AgentBootstrapContribution contribution)
    {
        using var parsed = JsonDocument.Parse(contribution.Files[".mcp.json"]);
        return parsed.RootElement.GetProperty("mcpServers").Clone();
    }
}
