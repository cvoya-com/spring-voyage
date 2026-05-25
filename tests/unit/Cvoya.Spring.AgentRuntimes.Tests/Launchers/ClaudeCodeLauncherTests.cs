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
/// Unit tests for <see cref="ClaudeCodeLauncher"/>.
/// </summary>
public class ClaudeCodeLauncherTests
{
    private const string DefaultOAuthToken = "sk-ant-oat-test-token";

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILlmCredentialResolver _credentialResolver;
    private readonly LauncherCallbackTestSupport _callbackSupport;
    private readonly ClaudeCodeLauncher _launcher;

    public ClaudeCodeLauncherTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _credentialResolver = Substitute.For<ILlmCredentialResolver>();
        _credentialResolver
            .ResolveAsync("anthropic", Cvoya.Spring.Core.Catalog.AuthMethod.Oauth, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: DefaultOAuthToken,
                Source: LlmCredentialSource.Tenant,
                SecretName: "anthropic-oauth"));

        _callbackSupport = new LauncherCallbackTestSupport();
        var scopeFactory = TestScopeFactory.For(_credentialResolver);
        _launcher = new ClaudeCodeLauncher(scopeFactory, _loggerFactory, _callbackSupport.Builder);
    }

    [Fact]
    public void Kind_IsClaudeCodeCli()
    {
        // #1732: launcher key matches the catalogue runtime's `Launcher`
        // field for the `claude` entry (claude-code-cli). The dispatcher
        // dictionary is keyed on this.
        _launcher.Kind.ShouldBe("claude-code-cli");
    }

    [Fact]
    public async Task PrepareAsync_ReturnsEnvVars()
    {
        // ADR-0055: the launcher emits env vars only; the workspace files
        // (CLAUDE.md / .mcp.json) move to ContributeBundleAsync.
        var context = CreateContext();

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
        // #2668: the Claude Code CLI never reads SPRING_SYSTEM_PROMPT —
        // the system prompt is delivered via CLAUDE.md from
        // ContributeBundleAsync. The launcher therefore no longer stamps
        // the env var.
        prep.EnvironmentVariables.ContainsKey("SPRING_SYSTEM_PROMPT").ShouldBeFalse(
            "Claude Code consumes its system prompt from CLAUDE.md (the bundle path), not SPRING_SYSTEM_PROMPT");
        _callbackSupport.AssertCallbackEnvironment(prep, context);

        prep.ExtraVolumeMounts.ShouldBeNull();
        prep.WorkingDirectory.ShouldBeNull(
            "leaving WorkingDirectory unset lets the dispatcher default to the per-member workspace mount");
    }

    [Fact]
    public async Task ContributeBundleAsync_ReturnsPlatformPromptFileAndMcpJson()
    {
        // ADR-0055 §3: launcher-owned in-workspace files live in the
        // bootstrap contribution, not on the launch spec. ADR-0057 §1:
        // the `.mcp.json` MCP server entry is `command`-typed (stdio),
        // not `http`-typed — the CLI spawns the sidecar binary in
        // MCP-server mode per turn rather than dialling the worker
        // across the network. #2672: the platform's system prompt
        // lives at `.spring/system-prompt.md` under ADR-0058 §2.2.2's
        // namespace, NOT the CLI's auto-discovered `CLAUDE.md` (that
        // filename is reserved for any project clone the agent makes
        // under its workspace).
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(),
            TestContext.Current.CancellationToken);

        contribution.Files.Keys.ShouldBe(
            new[] { ".spring/system-prompt.md", ".mcp.json" }, ignoreOrder: true);
        contribution.Files[".spring/system-prompt.md"].ShouldBe(TestAssembledSystemPrompt);
        contribution.Files.ShouldNotContainKey("CLAUDE.md",
            "the platform never writes the CLI's auto-discovered filename — that " +
            "collides with any project clone the agent makes under its workspace (#2672)");

        var parsed = JsonDocument.Parse(contribution.Files[".mcp.json"]).RootElement;
        var server = parsed.GetProperty("mcpServers").GetProperty("spring-voyage");

        // ADR-0057 §1: stdio MCP server. `command` is the Node binary
        // (on PATH in agent-base), `args` invokes the sidecar bundle
        // with the `mcp` argv token so it runs in MCP-server mode.
        server.GetProperty("command").GetString().ShouldBe("node");
        var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        args.ShouldBe(new[] { "/opt/spring-voyage/sidecar/dist/cli.js", "mcp" });

        // ADR-0057 §2: the MCP-server-mode child proxies onto the
        // worker's MCP endpoint, which is delivered via env.
        var env = server.GetProperty("env");
        env.GetProperty("SPRING_MCP_PROXY_URL").GetString().ShouldBe(BundleContextMcpEndpoint);
        env.GetProperty("SPRING_WORKSPACE_PATH").GetString()
            .ShouldBe(AgentWorkspaceContract.BuildMountPath(
                LauncherCallbackTestSupport.DefaultAgentAddress.Path));

        // ADR-0057 §3: no Authorization header — the CLI never sees the
        // per-turn token; only the spawned MCP-server-mode child reads
        // it from the workspace-resident token file.
        server.TryGetProperty("headers", out _).ShouldBeFalse(
            "stdio MCP servers carry no Authorization header — the CLI never holds the per-turn token");
        // And no `url` / `type: http` — the cross-network transport is
        // explicitly removed.
        server.TryGetProperty("url", out _).ShouldBeFalse(
            "stdio MCP servers carry no remote URL");

        contribution.PlatformFilePaths.ShouldBe(
            new[] { ".spring/system-prompt.md", ".mcp.json" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ContributeBundleAsync_EmptyAssembledPrompt_YieldsEmptyPromptFile()
    {
        // The bundle provider always supplies AssembledSystemPrompt — but
        // a synthetic empty prompt must materialise an empty
        // `.spring/system-prompt.md` rather than throwing.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(assembledSystemPrompt: string.Empty),
            TestContext.Current.CancellationToken);

        contribution.Files[".spring/system-prompt.md"].ShouldBe(string.Empty);
    }

    [Fact]
    public async Task ContributeBundleAsync_WritesOnlyTheSinglePlatformMcpServer()
    {
        // ADR-0051: one MCP server serves every sv.* tool — sv.messaging.*
        // included. The bundle no longer writes a second messaging server.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(),
            TestContext.Current.CancellationToken);

        var servers = GetMcpServers(contribution);
        servers.EnumerateObject().Select(property => property.Name)
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
        var context = CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldNotContainKey("SPRING_ORCHESTRATION_MCP_CONFIG");
        prep.EnvironmentVariables.ShouldNotContainKey("SPRING_MCP_CONFIG");
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
        // Default mode is Append (#2695) — argv carries the
        // `--append-system-prompt-file` flag pointing at
        // `.spring/system-prompt.md` (#2672). The bridge does
        // JSON.parse on SPRING_AGENT_ARGV (see
        // src/Cvoya.Spring.AgentSidecar/src/config.ts); round-tripping
        // through JsonSerializer is the contract.
        var context = CreateContext();
        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey("SPRING_AGENT_ARGV");
        var raw = prep.EnvironmentVariables["SPRING_AGENT_ARGV"];

        var argv = JsonSerializer.Deserialize<string[]>(raw);
        argv.ShouldNotBeNull();
        var workspaceNoSlash = AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId);
        var expectedMcpConfigPath = $"{workspaceNoSlash}/.mcp.json";
        var expectedSystemPromptPath = $"{workspaceNoSlash}/.spring/system-prompt.md";
        argv.ShouldBe(new[]
        {
            "claude",
            "--print",
            "--dangerously-skip-permissions",
            "--mcp-config",
            expectedMcpConfigPath,
            "--append-system-prompt-file",
            expectedSystemPromptPath,
        });
    }

    [Fact]
    public async Task PrepareAsync_AppendMode_UsesAppendSystemPromptFileFlag()
    {
        // #2695: explicit Append mode produces the append flag — the
        // CLI's coding-assistant baseline is preserved.
        var context = LauncherCallbackTestSupport.CreateContext(
            systemPromptMode: Cvoya.Spring.Core.Catalog.SystemPromptMode.Append);

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        var argv = JsonSerializer.Deserialize<string[]>(
            prep.EnvironmentVariables["SPRING_AGENT_ARGV"]);
        argv.ShouldNotBeNull();
        argv.ShouldContain("--append-system-prompt-file");
        argv.ShouldNotContain("--system-prompt-file",
            "the append and replace flags are mutually exclusive per the Claude CLI reference (#2695)");
    }

    [Fact]
    public async Task PrepareAsync_ReplaceMode_UsesSystemPromptFileFlag()
    {
        // #2695: Replace mode produces the replace flag — the CLI's
        // coding-assistant baseline is dropped entirely; the assembled
        // platform prompt is the whole system prompt. Used by routers,
        // PMs, analysts that opt into the override.
        var context = LauncherCallbackTestSupport.CreateContext(
            systemPromptMode: Cvoya.Spring.Core.Catalog.SystemPromptMode.Replace);

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        var argv = JsonSerializer.Deserialize<string[]>(
            prep.EnvironmentVariables["SPRING_AGENT_ARGV"]);
        argv.ShouldNotBeNull();
        argv.ShouldContain("--system-prompt-file");
        argv.ShouldNotContain("--append-system-prompt-file",
            "the append and replace flags are mutually exclusive per the Claude CLI reference (#2695)");

        // The flag is followed by the same `.spring/system-prompt.md`
        // path the Append-mode invocation uses — the file itself does
        // not move, only the flag selecting how the CLI consumes it.
        var workspaceNoSlash = AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId);
        var expectedSystemPromptPath = $"{workspaceNoSlash}/.spring/system-prompt.md";
        var flagIndex = Array.IndexOf(argv, "--system-prompt-file");
        argv[flagIndex + 1].ShouldBe(expectedSystemPromptPath);
    }

    [Fact]
    public async Task PrepareAsync_DefaultMode_FallsBackToAppend()
    {
        // #2695: when context.SystemPromptMode is left at the
        // dispatcher-applied default (Append), the launcher emits the
        // append flag — preserves today's behaviour after #2672.
        var context = CreateContext();
        context.SystemPromptMode.ShouldBe(Cvoya.Spring.Core.Catalog.SystemPromptMode.Append,
            "test support defaults SystemPromptMode to Append — matches the dispatcher's fallback");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        var argv = JsonSerializer.Deserialize<string[]>(
            prep.EnvironmentVariables["SPRING_AGENT_ARGV"]);
        argv.ShouldNotBeNull();
        argv.ShouldContain("--append-system-prompt-file");
    }

    [Fact]
    public async Task PrepareAsync_DefaultsA2APortAndResponseCapture()
    {
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        // Defaults are part of the wire contract — assert them so a
        // change is intentional and caught here rather than in PR 5.
        prep.A2APort.ShouldBe(8999);
        prep.ResponseCapture.ShouldBe(AgentResponseCapture.A2ATrace);
    }

    [Fact]
    public async Task PrepareAsync_SetsSpringWorkspacePath_ToPerMemberMountPath()
    {
        var context = CreateContext();
        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey(AgentWorkspaceContract.WorkspacePathEnvVar);
        prep.EnvironmentVariables[AgentWorkspaceContract.WorkspacePathEnvVar]
            .ShouldBe(AgentWorkspaceContract.BuildMountPath(context.AgentId));
    }

    [Fact]
    public async Task PrepareAsync_OAuthToken_InjectsClaudeCodeOAuthTokenEnvVar()
    {
        // #1714 step 2: the Claude launcher injects the resolved OAuth
        // token under CLAUDE_CODE_OAUTH_TOKEN, never ANTHROPIC_API_KEY.
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"].ShouldBe(DefaultOAuthToken);
        prep.EnvironmentVariables.ShouldNotContainKey(
            "ANTHROPIC_API_KEY",
            "ANTHROPIC_API_KEY is never injected by the Claude launcher (#1714) — that env var is only emitted by the Spring Voyage launcher when `provider: anthropic`.");
    }

    [Fact]
    public async Task PrepareAsync_UnitIdOnContext_ForwardsParsedGuidToResolver()
    {
        // #2251: when the dispatcher stamps the agent's owning unit id on
        // AgentLaunchContext, the launcher must parse it as a Guid and pass
        // it to ILlmCredentialResolver so the resolver can walk Tier 1
        // (unit) and the parent-unit chain before falling back to tenant
        // scope. Before #2251 every dispatch site left context.UnitId null
        // and unit-scoped tokens were silently ignored.
        var unitGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var context = LauncherCallbackTestSupport.CreateContext(unitId: unitGuid.ToString("N"));

        await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        await _credentialResolver.Received(1).ResolveAsync(
            "anthropic",
            Cvoya.Spring.Core.Catalog.AuthMethod.Oauth,
            Arg.Any<Guid?>(),
            Arg.Is<Guid?>(unit => unit == unitGuid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareAsync_NullUnitIdOnContext_PassesNullToResolver()
    {
        // Symmetric guard: a context without UnitId must surface as a null
        // unit slot to the resolver — the resolver's tenant-only fallback
        // path is the documented contract for agents without a parent unit.
        var context = LauncherCallbackTestSupport.CreateContext(unitId: null);

        await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        await _credentialResolver.Received(1).ResolveAsync(
            "anthropic",
            Cvoya.Spring.Core.Catalog.AuthMethod.Oauth,
            Arg.Any<Guid?>(),
            Arg.Is<Guid?>(unit => unit == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareAsync_ApiKey_FailsPreFlightWithGuidance()
    {
        // #1714 step 2: API keys are rejected on the Claude AgentRuntime path
        // (project does not run `claude --bare`). Operator guidance must
        // mention `claude setup-token` and the spring-voyage runtime fallback.
        _credentialResolver
            .ResolveAsync("anthropic", Cvoya.Spring.Core.Catalog.AuthMethod.Oauth, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: "sk-ant-api-not-allowed",
                Source: LlmCredentialSource.Tenant,
                SecretName: "anthropic-oauth"));

        var ex = await Should.ThrowAsync<SpringException>(
            () => _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("OAuth token");
        ex.Message.ShouldContain("claude setup-token");
        ex.Message.ShouldContain("spring-voyage");
        // #2189: producer tags the format-rejection precisely.
        ex.Data[SpringException.IssueCodeDataKey].ShouldBe("CredentialFormatRejected");
        ex.Data[SpringException.IssueSourceDataKey].ShouldBe("credential");
    }

    [Fact]
    public async Task PrepareAsync_MissingCredential_FailsPreFlightWithGuidance()
    {
        // #1714 step 2: the launcher fails BEFORE container launch when no
        // value resolved at agent / unit / parent-unit chain / tenant scope.
        _credentialResolver
            .ResolveAsync("anthropic", Cvoya.Spring.Core.Catalog.AuthMethod.Oauth, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: null,
                Source: LlmCredentialSource.NotFound,
                SecretName: "anthropic-oauth"));

        var ex = await Should.ThrowAsync<SpringException>(
            () => _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("anthropic-oauth");
        ex.Message.ShouldContain("agent, unit, parent-unit chain, or tenant scope");
        // #2189: producer tags the (code, source) on ex.Data so the
        // AgentActor catch attributes precisely.
        ex.Data[SpringException.IssueCodeDataKey].ShouldBe("CredentialMissing");
        ex.Data[SpringException.IssueSourceDataKey].ShouldBe("credential");
    }

    [Fact]
    public async Task PrepareAsync_MalformedCredential_FailsPreFlight()
    {
        // Pre-flight format check rejects values that are neither
        // sk-ant-oat… nor sk-ant-api… so the launcher does not waste a
        // network round-trip just to receive a 401.
        _credentialResolver
            .ResolveAsync("anthropic", Cvoya.Spring.Core.Catalog.AuthMethod.Oauth, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: "totally-not-a-key",
                Source: LlmCredentialSource.Tenant,
                SecretName: "anthropic-oauth"));

        await Should.ThrowAsync<SpringException>(
            () => _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrepareAsync_UnreadableCredential_FailsPreFlight()
    {
        _credentialResolver
            .ResolveAsync("anthropic", Cvoya.Spring.Core.Catalog.AuthMethod.Oauth, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: null,
                Source: LlmCredentialSource.Unreadable,
                SecretName: "anthropic-oauth"));

        await Should.ThrowAsync<SpringException>(
            () => _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken));
    }

    // #2668 / #2738: the launcher-level ConcurrentThreadsGuard tests
    // moved with the guard fold. The Claude Code CLI never reads
    // SPRING_SYSTEM_PROMPT — the guard now travels via
    // `.spring/system-prompt.md`, which AgentBootstrapBundleProvider
    // composes by threading `ConcurrentThreadsGuard: true` through
    // PromptAssemblyContext (#2738 — was a post-assemble
    // LauncherPromptFragments.Compose call pre-cutover) before handing
    // the assembled body to ContributeBundleAsync. The guard's delivery
    // is therefore covered by AgentBootstrapBundleProviderTests and the
    // in-band render shape by PromptAssemblerTests.

    /// <summary>
    /// #2682: the launcher contributes runtime-true workspace prose
    /// that names the env vars and CLI surface it actually wires up
    /// (workspace path, CLAUDE_CONFIG_DIR, MCP discovery file) and
    /// stays author-agnostic (no clone-paths or GitHub env vars).
    /// </summary>
    [Fact]
    public void GetWorkspacePromptFragment_NamesClaudeCodeRuntimeAndWorkspaceEnvVars()
    {
        var fragment = _launcher.GetWorkspacePromptFragment();

        fragment.ShouldNotBeNullOrWhiteSpace();
        fragment!.ShouldContain("Claude Code CLI");
        fragment.ShouldContain("`claude`");
        fragment.ShouldContain("$SPRING_WORKSPACE_PATH");
        // #2672: platform prompt reaches the CLI via the
        // append/replace flag on argv, not via auto-discovered
        // CLAUDE.md. The fragment must name the flag so authors know
        // the delivery channel.
        fragment.ShouldContain("--append-system-prompt-file");
        fragment.ShouldContain(".mcp.json");
        fragment.ShouldContain("$CLAUDE_CONFIG_DIR");
        // #2672: the fragment must NOT promise auto-discovered
        // CLAUDE.md — that filename is reserved for any project clone
        // the agent makes under its workspace.
        fragment.ShouldNotContain(
            "auto-discovers its system prompt from `CLAUDE.md`",
            customMessage: "the platform no longer writes the CLI's auto-discovered filename (#2672)");

        // Author-agnostic — no clones / GitHub env vars / worktree
        // conventions; those belong on the author's role-specific
        // instructions, not on the launcher.
        fragment.ShouldNotContain("GH_TOKEN");
        fragment.ShouldNotContain("GITHUB_TOKEN");
        fragment.ShouldNotContain("worktree");

        // #2742: the launcher fragment describes the CLI-universal
        // surface (runtime CLI, workspace mount, delivery flags, MCP
        // discovery, session-state env var). It MUST NOT assert that a
        // specific image bundles a specific tool set — multiple images
        // run against the same CLI in v0.1 (software-engineering,
        // program-management, claude-code-base, …), each with its own
        // tool inventory. Image-bundled tooling belongs in the
        // role-specific instructions for that image, not in the
        // platform-emitted launcher fragment.
        fragment.ShouldNotContain("standard image bundles");
        fragment.ShouldNotContain("`dotnet`");
        fragment.ShouldNotContain("`gh`");
        fragment.ShouldNotContain("`git`");
        fragment.ShouldNotContain("`python3`");
        // Containers / images are platform-internal concerns the agent
        // cannot act on — #2739 stripped this jargon from the platform
        // contract, and the launcher fragment should match.
        fragment.ShouldNotContain("Debian-based container");
        fragment.ShouldNotContain("Spring Voyage agent sidecar");
    }

    private const string BundleContextMcpEndpoint = "http://host.docker.internal:9999/mcp/";
    private const string TestAssembledSystemPrompt = "ASSEMBLED SYSTEM PROMPT FOR TEST";

    private static AgentLaunchContext CreateContext() =>
        LauncherCallbackTestSupport.CreateContext();

    private static AgentBootstrapContributionContext CreateBundleContext(
        string? instructions = "You are a helpful agent.",
        string assembledSystemPrompt = TestAssembledSystemPrompt)
    {
        var definition = new AgentDefinition(
            AgentId: LauncherCallbackTestSupport.DefaultAgentAddress.Path,
            Name: "Test Agent",
            Instructions: instructions,
            Execution: new AgentExecutionConfig(
                Runtime: "claude",
                Image: "ghcr.io/test/claude:latest"));
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
