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
    public async Task ContributeBundleAsync_ReturnsClaudeMdAndMcpJsonFiles()
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

        contribution.Files.Keys.ShouldBe(new[] { "CLAUDE.md", ".mcp.json" }, ignoreOrder: true);
        contribution.Files["CLAUDE.md"].ShouldBe(TestAssembledSystemPrompt);

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

        contribution.PlatformFilePaths.ShouldBe(new[] { "CLAUDE.md", ".mcp.json" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ContributeBundleAsync_EmptyAssembledPrompt_YieldsEmptyClaudeMd()
    {
        // The bundle provider always supplies AssembledSystemPrompt — but
        // a synthetic empty prompt must materialise an empty CLAUDE.md
        // rather than throwing.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(assembledSystemPrompt: string.Empty),
            TestContext.Current.CancellationToken);

        contribution.Files["CLAUDE.md"].ShouldBe(string.Empty);
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
        var context = CreateContext();
        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey("SPRING_AGENT_ARGV");
        var raw = prep.EnvironmentVariables["SPRING_AGENT_ARGV"];

        // The bridge does JSON.parse on this value (see
        // src/Cvoya.Spring.AgentSidecar/src/config.ts). Round-tripping it
        // through JsonSerializer is the contract. The argv carries
        // --mcp-config so the CLI loads the platform MCP server
        // independently of its spawn CWD.
        var argv = JsonSerializer.Deserialize<string[]>(raw);
        argv.ShouldNotBeNull();
        var expectedMcpConfigPath =
            $"{AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId)}/.mcp.json";
        argv.ShouldBe(new[]
        {
            "claude",
            "--print",
            "--dangerously-skip-permissions",
            "--mcp-config",
            expectedMcpConfigPath,
        });
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

    // #2668: the launcher-level ConcurrentThreadsGuard tests moved with
    // the guard fold. The Claude Code CLI never reads SPRING_SYSTEM_PROMPT
    // — the guard now travels via CLAUDE.md, which AgentBootstrapBundleProvider
    // composes by calling LauncherPromptFragments.Compose against the
    // assembled prompt before handing it to ContributeBundleAsync. The
    // guard's delivery is therefore covered by
    // AgentBootstrapBundleProviderTests.

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
