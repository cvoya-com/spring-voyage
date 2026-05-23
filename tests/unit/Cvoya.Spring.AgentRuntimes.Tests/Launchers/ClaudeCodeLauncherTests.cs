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
        // Issue #2493: every launcher prepends the always-on
        // ResponseDiscipline fragment; the user's prompt is the tail of
        // the assembled body.
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldEndWith(context.Prompt);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldContain("Spring Voyage runtime guard — response discipline");
        _callbackSupport.AssertCallbackEnvironment(prep, context);

        prep.ExtraVolumeMounts.ShouldBeNull();
        prep.WorkingDirectory.ShouldBeNull(
            "leaving WorkingDirectory unset lets the dispatcher default to the per-member workspace mount");
    }

    [Fact]
    public async Task ContributeBundleAsync_ReturnsClaudeMdAndMcpJsonFiles()
    {
        // ADR-0055 §3: launcher-owned in-workspace files live in the
        // bootstrap contribution, not on the launch spec. Per the silent-
        // dispatch fix, CLAUDE.md must include the ResponseDiscipline
        // guard (Claude Code auto-reads CLAUDE.md from CWD; nothing
        // passes SPRING_SYSTEM_PROMPT to the CLI), so the agent sees the
        // ADR-0056 contract that stdout is diagnostic only.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(),
            TestContext.Current.CancellationToken);

        contribution.Files.Keys.ShouldBe(new[] { "CLAUDE.md", ".mcp.json" }, ignoreOrder: true);
        var claudeMd = contribution.Files["CLAUDE.md"];
        claudeMd.ShouldContain("Spring Voyage runtime guard — response discipline");
        claudeMd.ShouldContain("sv.messaging.send");
        claudeMd.ShouldEndWith("You are a helpful agent.");

        var parsed = JsonDocument.Parse(contribution.Files[".mcp.json"]).RootElement;
        var server = parsed.GetProperty("mcpServers").GetProperty("spring-voyage");
        server.GetProperty("url").GetString().ShouldBe(BundleContextMcpEndpoint);
        // ADR-0052 §4 / ADR-0055: the bundle writes an empty Authorization
        // placeholder — the sidecar rewrites it per turn from the A2A
        // message metadata.
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .ShouldBe("Bearer ");

        contribution.PlatformFilePaths.ShouldBe(new[] { "CLAUDE.md", ".mcp.json" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ContributeBundleAsync_NullInstructions_YieldsGuardOnlyClaudeMd()
    {
        // AgentDefinition.Instructions is nullable; the bundle still
        // materialises CLAUDE.md — now seeded with the platform guards
        // even when the author's instructions are absent. Without this,
        // a sparsely-configured agent would launch with no
        // response-discipline context and silently dispatch.
        var contribution = await _launcher.ContributeBundleAsync(
            CreateBundleContext(instructions: null),
            TestContext.Current.CancellationToken);

        var claudeMd = contribution.Files["CLAUDE.md"];
        claudeMd.ShouldContain("Spring Voyage runtime guard — response discipline");
        claudeMd.ShouldContain("sv.messaging.send");
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
    public async Task PrepareAsync_SetsSpringMcpConfigPath_UnderPerMemberMount()
    {
        // ADR-0052 §4: the dead SPRING_ORCHESTRATION_MCP_CONFIG env var is
        // gone; SPRING_MCP_CONFIG points the bridge at the `.mcp.json` it
        // rewrites per turn with the delivered MCP session token.
        var context = CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldNotContainKey("SPRING_ORCHESTRATION_MCP_CONFIG");
        prep.EnvironmentVariables["SPRING_MCP_CONFIG"].ShouldBe(
            $"{AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId)}/.mcp.json");
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

    [Fact]
    public async Task PrepareAsync_ConcurrentThreadsTrue_PrependsBothGuardsToSystemPromptEnv()
    {
        // #2096 / ADR-0041 + #2493: when concurrent_threads is on, the
        // assembled prompt the model sees via SPRING_SYSTEM_PROMPT MUST
        // start with the always-on ResponseDiscipline guard and
        // additionally carry the ConcurrentThreadsGuard. The user's
        // prompt body is preserved — the guards compose, they do not
        // replace.
        var context = CreateContext() with { ConcurrentThreads = true };

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
        var context = CreateContext() with { ConcurrentThreads = false };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldStartWith("## Spring Voyage runtime guard — response discipline");
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldEndWith(context.Prompt);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldNotContain("concurrent_threads is on");
    }

    private const string BundleContextMcpEndpoint = "http://host.docker.internal:9999/mcp/";

    private static AgentLaunchContext CreateContext() =>
        LauncherCallbackTestSupport.CreateContext();

    private static AgentBootstrapContributionContext CreateBundleContext(
        string? instructions = "You are a helpful agent.")
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
            McpEndpoint: BundleContextMcpEndpoint);
    }

    private static JsonElement GetMcpServers(AgentBootstrapContribution contribution)
    {
        using var parsed = JsonDocument.Parse(contribution.Files[".mcp.json"]);
        return parsed.RootElement.GetProperty("mcpServers").Clone();
    }
}
