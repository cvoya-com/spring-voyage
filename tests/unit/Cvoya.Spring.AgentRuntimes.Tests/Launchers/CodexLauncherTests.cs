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
        // #3106: the dispatcher is CWD-independent (null WorkingDirectory ⇒
        // image WORKDIR wins), so the Codex launcher pins CWD to the
        // per-member workspace mount itself — `AGENTS.md`, `.mcp.json`, and the
        // per-turn mcp-token are discovered relative to CWD.
        prep.WorkingDirectory.ShouldBe(
            AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId),
            "the Codex CLI discovers AGENTS.md / .mcp.json relative to CWD, so the launcher pins CWD to the workspace mount");
    }

    [Fact]
    public async Task ContributeBundleAsync_ReturnsAgentsMdAndCodexConfigToml()
    {
        // #3122: Codex discovers MCP servers from `$CODEX_HOME/config.toml`,
        // NOT from `.mcp.json`. ADR-0055 §3: launcher-owned in-workspace files
        // live in the bootstrap contribution. ADR-0057 §1: the MCP server
        // entry is `command`-typed (stdio) — the CLI spawns the sidecar binary
        // in MCP-server mode per turn rather than dialling the worker over HTTP.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(),
            TestContext.Current.CancellationToken);

        contribution.Files.Keys.ShouldBe(new[] { "AGENTS.md", ".codex/config.toml" }, ignoreOrder: true);
        contribution.Files["AGENTS.md"].ShouldBe(TestAssembledSystemPrompt);

        var toml = contribution.Files[".codex/config.toml"];

        // The single platform server table, stdio shape.
        toml.ShouldContain("[mcp_servers.spring-voyage]");
        toml.ShouldContain("command = \"node\"");
        toml.ShouldContain("args = [\"/opt/spring-voyage/sidecar/dist/cli.js\", \"mcp\"]");

        // Static env block carries the proxy URL + workspace path.
        toml.ShouldContain("[mcp_servers.spring-voyage.env]");
        toml.ShouldContain($"SPRING_MCP_PROXY_URL = \"{BundleContextMcpEndpoint}\"");
        toml.ShouldContain(
            "SPRING_WORKSPACE_PATH = \"" +
            AgentWorkspaceContract.BuildMountPath(LauncherCallbackTestSupport.DefaultAgentAddress.Path) +
            "\"");

        // #3122 / #3000: the per-turn token path rides `env_vars` (forwarded
        // from Codex's own process env into the MCP child), NOT the static
        // `env` block (which is fixed at bundle-build time and cannot carry a
        // per-turn value). This is what gives Codex per-turn token isolation.
        toml.ShouldContain("env_vars = [\"SPRING_MCP_TOKEN_PATH\"]");
        toml.ShouldNotContain("SPRING_MCP_TOKEN_PATH = ");

        // ADR-0057 §3: no HTTP transport / bearer token in the config — the
        // CLI never sees the per-turn token; only the spawned MCP-server-mode
        // child holds it. (Match a `url =` key on its own line so the
        // `SPRING_MCP_PROXY_URL` env key, which is the stdio proxy endpoint,
        // does not trip the assertion.)
        System.Text.RegularExpressions.Regex.IsMatch(
            toml, @"^\s*url\s*=", System.Text.RegularExpressions.RegexOptions.Multiline)
            .ShouldBeFalse("the stdio MCP server must not declare an HTTP `url` transport");
        toml.ShouldNotContain("bearer_token");

        contribution.PlatformFilePaths.ShouldBe(new[] { "AGENTS.md", ".codex/config.toml" }, ignoreOrder: true);
    }

    [Fact]
    public async Task PrepareAsync_PointsCodexHomeAtWorkspaceConfigDir()
    {
        // #3122: CODEX_HOME must resolve to the same `.codex` dir the bundle
        // writes `config.toml` into, so the CLI reads the platform MCP server.
        // Anchored under the per-agent workspace mount so it survives restart.
        var context = LauncherCallbackTestSupport.CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["CODEX_HOME"].ShouldBe(
            $"{AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId)}/.codex");
    }

    [Fact]
    public async Task PrepareAsync_SetsSpringAgentArgv_AsJsonEncodedArrayOfStrings()
    {
        // #2119: until this issue the launcher left SPRING_AGENT_ARGV unset
        // and the agent-base bridge had nothing to spawn — Codex agent
        // containers literally could not execute end-to-end (the same defect
        // #2108 fixed for Gemini). The default argv must be JSON-encoded (the
        // bridge does JSON.parse, see
        // `src/Cvoya.Spring.AgentSidecar/src/config.ts:parseArgv`).
        //
        // Each flag is pinned here so accidental drift from the upstream
        // `codex exec --help` surface (codex-cli 0.136.x) surfaces in
        // unit-test failure rather than at integration time. See the
        // doc-comment on `CodexLauncher.BaseCodexArgv` for the per-flag
        // rationale. No PROMPT token: `codex exec` reads the user prompt from
        // stdin, which the bridge pipes in.
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "Be helpful.",
            mcpToken: "codex-secret-token");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey("SPRING_AGENT_ARGV");
        var raw = prep.EnvironmentVariables["SPRING_AGENT_ARGV"];

        var argv = JsonSerializer.Deserialize<string[]>(raw);
        argv.ShouldNotBeNull();
        argv.ShouldBe(new[]
        {
            "codex",
            "exec",
            // #3123: `--json` makes codex exec emit its JSONL event stream,
            // which the sidecar parses (SPRING_AGENT_OUTPUT_FORMAT=stream-json).
            "--json",
            "--dangerously-bypass-approvals-and-sandbox",
            "--skip-git-repo-check",
        });
    }

    [Fact]
    public async Task PrepareAsync_DoesNotSetThreadBindingArgEnvVars()
    {
        // #2118: the catalogue binds Codex as `threadBinding: { kind: none }`
        // because the Codex CLI exposes no caller-supplied-session-id surface
        // at create time (`--conversation-id` never existed; `codex exec
        // resume <UUID>` only resumes an already-existing id). The launcher
        // must NOT emit SPRING_THREAD_ID_ARG_CREATE / _RESUME — those tell the
        // bridge to append a session-id flag, which Codex cannot accept. Their
        // presence would make the bridge build an argv Codex rejects.
        var context = LauncherCallbackTestSupport.CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldNotContainKey("SPRING_THREAD_ID_ARG_CREATE");
        prep.EnvironmentVariables.ShouldNotContainKey("SPRING_THREAD_ID_ARG_RESUME");
    }

    [Fact]
    public async Task PrepareAsync_SetsOutputFormatStreamJsonHint()
    {
        // #3123: `codex exec --json` emits Codex's JSONL event stream, so the
        // launcher sets SPRING_AGENT_OUTPUT_FORMAT=stream-json — the sidecar's
        // shape-driven parser recognises Codex's events (thread.started /
        // turn.started / item.completed / turn.completed) alongside the
        // Claude/Gemini schemas. It must NOT be `json` (that path is built for
        // Claude's single-object `--output-format json` result).
        var context = LauncherCallbackTestSupport.CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_AGENT_OUTPUT_FORMAT"].ShouldBe("stream-json");
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
        // ADR-0054: one MCP server serves every sv.* tool — sv.messaging.*
        // included. The config.toml carries exactly one `[mcp_servers.<name>]`
        // table (#3122): the platform's `spring-voyage` server.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(),
            TestContext.Current.CancellationToken);

        var toml = contribution.Files[".codex/config.toml"];
        var serverTables = System.Text.RegularExpressions.Regex
            .Matches(toml, @"^\[mcp_servers\.([A-Za-z0-9_-]+)\]\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToArray();
        serverTables.ShouldBe(new[] { "spring-voyage" });
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
        // #3122: Codex reads its MCP server set from config.toml under
        // $CODEX_HOME, not from .mcp.json.
        fragment.ShouldContain("config.toml");
        fragment.ShouldContain("$CODEX_HOME");
        fragment.ShouldNotContain(".mcp.json");
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
}
