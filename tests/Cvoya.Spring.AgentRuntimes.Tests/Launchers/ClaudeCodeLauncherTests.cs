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
    public async Task PrepareAsync_ReturnsWorkspaceFilesAndEnvVars()
    {
        // The launcher must not write to the local filesystem — workspace
        // materialisation lives in the dispatcher (issue #1042). An earlier
        // revision snapshot Path.GetTempPath() before/after PrepareAsync
        // to assert that, but the assertion races with any parallel test
        // (in any assembly) that writes under /tmp, producing a recurring
        // CI flake (#1082). The contract is now enforced by code review
        // on the launcher implementation, which is pure-functional
        // dictionary construction.
        var context = CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.WorkspaceMountPath.ShouldBe("/workspace");
        prep.WorkspaceFiles.Keys.ShouldBe(new[] { "CLAUDE.md", ".mcp.json" }, ignoreOrder: true);
        prep.WorkspaceFiles["CLAUDE.md"].ShouldBe(context.Prompt);

        var parsed = JsonDocument.Parse(prep.WorkspaceFiles[".mcp.json"]).RootElement;
        var server = parsed.GetProperty("mcpServers").GetProperty("spring-voyage");
        server.GetProperty("url").GetString().ShouldBe(context.McpEndpoint);
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .ShouldBe("Bearer top-secret-token");

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
        prep.WorkingDirectory.ShouldBeNull(
            "leaving WorkingDirectory unset lets the dispatcher default to WorkspaceMountPath");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_OrchestrationToolsNullOrEmpty_WritesOnlySpringVoyageMcpServer(
        bool useEmptyTools)
    {
        var context = CreateContext(
            useEmptyTools ? Array.Empty<OrchestrationToolDescriptor>() : null);

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        var servers = GetMcpServers(prep);
        servers.EnumerateObject().Select(property => property.Name)
            .ShouldBe(new[] { "spring-voyage" });
    }

    [Fact]
    public async Task PrepareAsync_OrchestrationToolsPresent_WritesSpringOrchestrationMcpServer()
    {
        var context = CreateContext(CreateOrchestrationTools());

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        var servers = GetMcpServers(prep);
        servers.EnumerateObject().Select(property => property.Name)
            .ShouldBe(new[] { "spring-voyage", "spring-orchestration" });

        var orchestration = servers.GetProperty("spring-orchestration");
        orchestration.GetProperty("type").GetString().ShouldBe("http");
        orchestration.GetProperty("url").GetString()
            .ShouldBe(LauncherCallbackTestSupport.OrchestrationMcpUrl);
        orchestration.GetProperty("headers").GetProperty("Authorization").GetString()
            .ShouldBe(
                $"Bearer {prep.EnvironmentVariables[AgentCallbackEnvironmentContract.CallbackTokenEnvVar]}");
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
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey("SPRING_AGENT_ARGV");
        var raw = prep.EnvironmentVariables["SPRING_AGENT_ARGV"];

        // The bridge does JSON.parse on this value (see
        // deployment/agent-sidecar/src/config.ts). Round-tripping it
        // through JsonSerializer is the contract.
        var argv = JsonSerializer.Deserialize<string[]>(raw);
        argv.ShouldNotBeNull();
        argv.ShouldBe(new[]
        {
            "claude",
            "--print",
            "--dangerously-skip-permissions",
            "--output-format",
            "stream-json",
        });
    }

    [Fact]
    public async Task PrepareAsync_SetsStdinPayload_ToTheAssembledPrompt()
    {
        var context = CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        // The bridge will pipe this on `claude`'s stdin (PR 5). It must
        // carry the same prompt body the launcher already exposes via
        // CLAUDE.md and SPRING_SYSTEM_PROMPT — no new format.
        prep.StdinPayload.ShouldBe(context.Prompt);
        prep.StdinPayload.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PrepareAsync_DefaultsA2APortAndResponseCapture()
    {
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        // Defaults are part of the wire contract — assert them so a
        // change is intentional and caught here rather than in PR 5.
        prep.A2APort.ShouldBe(8999);
        prep.ResponseCapture.ShouldBe(AgentResponseCapture.A2A);
    }

    [Fact]
    public async Task PrepareAsync_SetsSpringWorkspacePath_ToCanonicalMountPath()
    {
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldContainKey(AgentWorkspaceContract.WorkspacePathEnvVar);
        prep.EnvironmentVariables[AgentWorkspaceContract.WorkspacePathEnvVar]
            .ShouldBe(AgentWorkspaceContract.WorkspaceMountPath);
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
    public async Task PrepareAsync_ConcurrentThreadsTrue_PrependsGuardToCLAUDEmd_AndStdin()
    {
        // #2096 / ADR-0041: when concurrent_threads is on, the assembled
        // prompt the model sees (CLAUDE.md, SPRING_SYSTEM_PROMPT,
        // StdinPayload) MUST start with the shared launcher guard. The
        // user's prompt body is preserved in full — the guard composes,
        // it does not replace.
        var context = CreateContext() with { ConcurrentThreads = true };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.WorkspaceFiles["CLAUDE.md"].ShouldStartWith("## Spring Voyage runtime guard");
        prep.WorkspaceFiles["CLAUDE.md"].ShouldContain(context.Prompt);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldStartWith("## Spring Voyage runtime guard");
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldContain(context.Prompt);
        prep.StdinPayload.ShouldStartWith("## Spring Voyage runtime guard");
        prep.StdinPayload.ShouldContain(context.Prompt);
    }

    [Fact]
    public async Task PrepareAsync_ConcurrentThreadsFalse_LeavesPromptVerbatim()
    {
        // The guard MUST NOT fire when the agent stays on the safe-default
        // mode — we don't want to bias every agent away from valid
        // patterns just because the launcher has a guard available.
        var context = CreateContext() with { ConcurrentThreads = false };

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.WorkspaceFiles["CLAUDE.md"].ShouldBe(context.Prompt);
        prep.WorkspaceFiles["CLAUDE.md"].ShouldNotContain("Spring Voyage runtime guard");
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldBe(context.Prompt);
        prep.StdinPayload.ShouldBe(context.Prompt);
    }

    private static AgentLaunchContext CreateContext(
        OrchestrationToolDescriptor[]? orchestrationTools = null) =>
        LauncherCallbackTestSupport.CreateContext() with { OrchestrationTools = orchestrationTools };

    private static JsonElement GetMcpServers(AgentLaunchSpec prep)
    {
        using var parsed = JsonDocument.Parse(prep.WorkspaceFiles[".mcp.json"]);
        return parsed.RootElement.GetProperty("mcpServers").Clone();
    }

    private static OrchestrationToolDescriptor[] CreateOrchestrationTools() =>
    [
        new(OrchestrationToolName.ListChildren, default, default),
        new(OrchestrationToolName.InspectChild, default, default),
        new(OrchestrationToolName.DelegateToChild, default, default),
        new(OrchestrationToolName.FanoutToChildren, default, default),
        new(OrchestrationToolName.QueryChildStatus, default, default),
    ];
}
