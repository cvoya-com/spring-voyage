// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Tests.Launchers;

using Cvoya.Spring.AgentRuntimes.Launchers;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.ModelProviders;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="A2AProcessLauncher"/> (ADR-0066) — the generic,
/// image-agnostic, always-on engine launcher.
/// </summary>
public class A2AProcessLauncherTests
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILlmCredentialResolver _credentialResolver;
    private readonly LauncherCallbackTestSupport _callbackSupport;
    private readonly A2AProcessLauncher _launcher;

    public A2AProcessLauncherTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _credentialResolver = Substitute.For<ILlmCredentialResolver>();
        _callbackSupport = new LauncherCallbackTestSupport();
        var scopeFactory = TestScopeFactory.For(_credentialResolver);
        _launcher = new A2AProcessLauncher(scopeFactory, _loggerFactory, _callbackSupport.Builder);
    }

    private void SeedTenantSecret(string providerId, string secretName, string value)
    {
        // Match any auth method: anthropic resolves OAuth, the API-key
        // providers resolve api-key — the launcher picks per provider.
        _credentialResolver
            .ResolveAsync(providerId, Arg.Any<Cvoya.Spring.Core.Catalog.AuthMethod>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: value,
                Source: LlmCredentialSource.Tenant,
                SecretName: secretName));
    }

    [Fact]
    public void Kind_IsA2AProcess()
    {
        _launcher.Kind.ShouldBe("a2a-process");
    }

    [Fact]
    public async Task PrepareAsync_SetsThreadIdPortAndWorkspace()
    {
        var context = CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_THREAD_ID"].ShouldBe(context.ThreadId);
        prep.EnvironmentVariables["AGENT_PORT"].ShouldBe("8999");
        prep.EnvironmentVariables[AgentWorkspaceContract.WorkspacePathEnvVar]
            .ShouldBe(AgentWorkspaceContract.BuildMountPath(context.AgentId));
        _callbackSupport.AssertCallbackEnvironment(prep, context);
    }

    [Fact]
    public async Task PrepareAsync_LeavesArgvEmpty_SoImageEntrypointRuns()
    {
        // Image-agnostic: the engine image's own ENTRYPOINT/CMD starts the
        // A2A server. An empty argv tells ContainerConfigBuilder to keep the
        // image default command.
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        prep.Argv.ShouldNotBeNull();
        prep.Argv.ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_NoProvider_ResolvesNoCredential()
    {
        // A deterministic engine uses no LLM. With no provider pinned the
        // launcher injects no credential and never consults the resolver.
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldNotContainKey("ANTHROPIC_API_KEY");
        prep.EnvironmentVariables.ShouldNotContainKey("OPENAI_API_KEY");
        await _credentialResolver.DidNotReceiveWithAnyArgs().ResolveAsync(
            Arg.Any<string>(),
            Arg.Any<Cvoya.Spring.Core.Catalog.AuthMethod>(),
            Arg.Any<Guid?>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareAsync_AnthropicProvider_InjectsOAuthToken()
    {
        // The a2a-process runtime co-hosts the Claude CLI and authenticates by
        // OAuth (like claude-code), so anthropic resolves CLAUDE_CODE_OAUTH_TOKEN,
        // not an API key.
        SeedTenantSecret("anthropic", "claude-code-oauth-token", "sk-ant-oat-fake");
        var context = MakeContext("anthropic", "claude-opus-4-8");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"].ShouldBe("sk-ant-oat-fake");
        prep.EnvironmentVariables.ShouldNotContainKey("ANTHROPIC_API_KEY");
    }

    [Fact]
    public async Task PrepareAsync_AnthropicProvider_ResolvesViaOAuthNotApiKey()
    {
        // Pins the auth method: anthropic on a2a-process is OAuth, matching the
        // catalogue edge and the co-hosted Claude CLI's CLAUDE_CODE_OAUTH_TOKEN.
        SeedTenantSecret("anthropic", "claude-code-oauth-token", "sk-ant-oat-fake");

        await _launcher.PrepareAsync(
            MakeContext("anthropic", "claude-opus-4-8"), TestContext.Current.CancellationToken);

        await _credentialResolver.Received().ResolveAsync(
            "anthropic",
            Cvoya.Spring.Core.Catalog.AuthMethod.Oauth,
            Arg.Any<Guid?>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareAsync_UnknownProvider_ThrowsWithGuidance()
    {
        var context = MakeContext("acme", "acme-1");

        var ex = await Should.ThrowAsync<SpringException>(
            () => _launcher.PrepareAsync(context, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("acme");
        ex.Message.ShouldContain("anthropic, openai, google");
        ex.Data[SpringException.IssueCodeDataKey].ShouldBe("ConfigurationIncomplete");
    }

    [Fact]
    public async Task PrepareAsync_MissingCredential_LaunchesWithoutCredential()
    {
        // Best-effort (ADR-0066): a deterministic engine declares a model only
        // to satisfy --strict but may make no LLM calls, so an unresolved
        // credential must not block boot. The launcher logs and continues —
        // no throw, and no credential env var is injected.
        _credentialResolver
            .ResolveAsync("anthropic", Arg.Any<Cvoya.Spring.Core.Catalog.AuthMethod>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: null,
                Source: LlmCredentialSource.NotFound,
                SecretName: "claude-code-oauth-token"));
        var context = MakeContext("anthropic", "claude-opus-4-8");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldNotContainKey("CLAUDE_CODE_OAUTH_TOKEN");
        prep.EnvironmentVariables.ShouldNotContainKey("ANTHROPIC_API_KEY");
    }

    [Fact]
    public async Task ContributeBundleAsync_WritesPlatformPromptAndNoMcpConfig()
    {
        var definition = new AgentDefinition(
            AgentId: LauncherCallbackTestSupport.DefaultAgentAddress.Path,
            Name: "Engine",
            Instructions: "x",
            Execution: new AgentExecutionConfig(
                Runtime: "a2a-process",
                Image: "ghcr.io/cvoya-com/spring-voyage-langgraph-orchestrator:latest"));

        const string assembled = "ASSEMBLED PROMPT";
        var contribution = await _launcher.ContributeBundleAsync(
            new AgentBootstrapContributionContext(
                AgentId: LauncherCallbackTestSupport.DefaultAgentAddress.Path,
                Definition: definition,
                McpEndpoint: "http://host.docker.internal:9999/mcp/",
                AssembledSystemPrompt: assembled),
            TestContext.Current.CancellationToken);

        contribution.Files.ShouldContainKeyAndValue(".spring/system-prompt.md", assembled);
        contribution.Files.ShouldNotContainKey(".mcp.json");
        contribution.PlatformFilePaths.ShouldBe(new[] { ".spring/system-prompt.md" });
    }

    [Fact]
    public async Task PrepareAsync_LeavesWorkingDirectoryNull()
    {
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);
        prep.WorkingDirectory.ShouldBeNull();
    }

    [Fact]
    public void GetWorkspacePromptFragment_NamesWorkspaceAndDurableToken()
    {
        var prose = _launcher.GetWorkspacePromptFragment();

        prose.ShouldNotBeNullOrWhiteSpace();
        prose!.ShouldContain("$SPRING_WORKSPACE_PATH");
        prose.ShouldContain("$SPRING_MCP_URL");
        // ADR-0066 §2: an always-on engine uses a durable, agent-scoped token
        // for calls at any time (incl. timer-triggered), not a per-turn token.
        prose.ShouldContain("$SPRING_MCP_TOKEN");
        prose.ShouldContain("timer");
    }

    private static AgentLaunchContext MakeContext(string provider, string model) =>
        LauncherCallbackTestSupport.CreateContext(
            prompt: "## System",
            mcpToken: "t",
            provider: provider,
            model: model);

    private static AgentLaunchContext CreateContext() =>
        LauncherCallbackTestSupport.CreateContext(
            prompt: "## System",
            mcpToken: "test-token-xyz");
}
