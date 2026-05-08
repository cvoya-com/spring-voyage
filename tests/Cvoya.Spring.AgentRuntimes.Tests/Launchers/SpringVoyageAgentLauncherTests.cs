// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Tests.Launchers;

using Cvoya.Spring.AgentRuntimes.Launchers;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.ModelProviders;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="SpringVoyageAgentLauncher"/>.
/// </summary>
public class SpringVoyageAgentLauncherTests
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<OllamaOptions> _ollamaOptions;
    private readonly ILlmCredentialResolver _credentialResolver;
    private readonly LauncherCallbackTestSupport _callbackSupport;
    private readonly SpringVoyageAgentLauncher _launcher;

    public SpringVoyageAgentLauncherTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _ollamaOptions = Options.Create(new OllamaOptions
        {
            DefaultModel = "llama3.2:3b",
            BaseUrl = "http://spring-ollama:11434",
        });

        _credentialResolver = Substitute.For<ILlmCredentialResolver>();

        _callbackSupport = new LauncherCallbackTestSupport();
        var scopeFactory = TestScopeFactory.For(_credentialResolver);
        _launcher = new SpringVoyageAgentLauncher(
            _ollamaOptions, scopeFactory, _loggerFactory, _callbackSupport.Builder);
    }

    private void SeedTenantSecret(string providerId, string secretName, string value)
    {
        // ADR-0038: Spring Voyage launcher always resolves via the
        // (provider, ApiKey) edge — its catalogue entries all carry
        // authMethod: api-key.
        _credentialResolver
            .ResolveAsync(providerId, Cvoya.Spring.Core.Catalog.AuthMethod.ApiKey, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: value,
                Source: LlmCredentialSource.Tenant,
                SecretName: secretName));
    }

    [Fact]
    public void Kind_IsSpringVoyageAgent()
    {
        // ADR-0038: launcher key matches the catalogue runtime entry's
        // `launcher` field. The Spring Voyage runtime entry's launcher is
        // `spring-voyage-agent`.
        _launcher.Kind.ShouldBe("spring-voyage-agent");
    }

    // Issue #1042: launchers must not materialise workspace dirs on the
    // worker side — the dispatcher owns that. An earlier revision verified
    // this with a Path.GetTempPath() before/after snapshot, but that
    // assertion races with any parallel test (in any assembly) that writes
    // under /tmp, producing a recurring CI flake (#1082). The contract is
    // now enforced by code review on the launcher implementation, which is
    // pure-functional dictionary construction; PrepareAsync_ProvidesEmptyWorkspace
    // below still pins WorkspaceMountPath = /workspace.

    [Fact]
    public async Task PrepareAsync_SetsRequiredEnvVars()
    {
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
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldBe(context.Prompt);
        _callbackSupport.AssertCallbackEnvironment(prep, context);
        // #1327: SPRING_MODEL and SPRING_LLM_PROVIDER are now D1-spec-declared (§ 2.2.1).
        prep.EnvironmentVariables["SPRING_MODEL"].ShouldBe("llama3.2:3b");
        prep.EnvironmentVariables["SPRING_LLM_PROVIDER"].ShouldBe("ollama");
        prep.EnvironmentVariables["AGENT_PORT"].ShouldBe("8999");
        // #1328: OLLAMA_ENDPOINT removed from launcher; llm-ollama.yaml now reads SPRING_LLM_PROVIDER_URL.
        prep.EnvironmentVariables.ShouldNotContainKey("OLLAMA_ENDPOINT",
            "OLLAMA_ENDPOINT must not be emitted by the launcher after #1328; " +
            "the Dapr Conversation YAML now reads SPRING_LLM_PROVIDER_URL.");
    }

    [Fact]
    public async Task PrepareAsync_ProvidesEmptyWorkspace()
    {
        // The Dapr Agent receives its prompt via SPRING_SYSTEM_PROMPT — so the
        // requested workspace is empty (the dispatcher still mounts an empty
        // dir at /workspace to keep the launch shape uniform across launchers).
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        prep.WorkspaceFiles.ShouldBeEmpty();
        prep.WorkspaceMountPath.ShouldBe("/workspace");
    }

    [Fact]
    public async Task PrepareAsync_LeavesWorkingDirectoryNull_SoImageDefaultIsKept()
    {
        // #1159: the Dapr Agent image's CMD is `python agent.py` relative
        // to its image WORKDIR (/app). The launcher must NOT set a
        // WorkingDirectory — combined with WorkspaceFiles being empty,
        // ContainerConfigBuilder will then leave the container workdir
        // unset and the image default applies. If either of those two
        // signals flips, `python: can't open file '/workspace/agent.py'`
        // returns and the container exits within ~40ms.
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        prep.WorkingDirectory.ShouldBeNull();
        prep.WorkspaceFiles.ShouldBeEmpty();
    }

    /// <summary>
    /// #1328: OLLAMA_ENDPOINT must never appear in the launcher's env vars —
    /// the Dapr Conversation component YAML now reads SPRING_LLM_PROVIDER_URL.
    /// This holds regardless of whether OllamaOptions.BaseUrl is configured.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_NeverEmitsOllamaEndpoint_Regardless_Of_BaseUrl()
    {
        var options = Options.Create(new OllamaOptions { DefaultModel = "phi3:mini", BaseUrl = "" });
        var scopeFactory = TestScopeFactory.For(_credentialResolver);
        var launcher = new SpringVoyageAgentLauncher(
            options, scopeFactory, _loggerFactory, _callbackSupport.Builder);
        var context = CreateContext();

        var prep = await launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldNotContainKey("OLLAMA_ENDPOINT",
            "OLLAMA_ENDPOINT must not be emitted by the launcher after #1328.");
        prep.EnvironmentVariables["SPRING_MODEL"].ShouldBe("phi3:mini");
    }

    [Fact]
    public async Task PrepareAsync_UsesProviderAndModelFromLaunchContext_WhenProvided()
    {
        // #480 step 5: when the AgentDefinition specifies a provider/model,
        // SpringVoyageAgentLauncher must forward them to the container env vars so the
        // Python Dapr Agent binds to the matching Conversation component.
        SeedTenantSecret("openai", "openai-api-key", "sk-openai-fake");
        var context = LauncherCallbackTestSupport.CreateContext(
            prompt: "prompt",
            mcpToken: "t",
            provider: "openai",
            model: "gpt-4o-mini");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_LLM_PROVIDER"].ShouldBe("openai");
        prep.EnvironmentVariables["SPRING_MODEL"].ShouldBe("gpt-4o-mini");
    }

    [Fact]
    public async Task PrepareAsync_FallsBackToOllamaDefaults_WhenLaunchContextLeavesProviderNull()
    {
        // Back-compat path: AgentDefinitions that predate the provider/model
        // fields must keep working. The launcher falls back to Ollama with the
        // configured OllamaOptions.DefaultModel so nothing regresses.
        var context = CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_LLM_PROVIDER"].ShouldBe("ollama");
        prep.EnvironmentVariables["SPRING_MODEL"].ShouldBe("llama3.2:3b");
    }

    [Fact]
    public async Task PrepareAsync_SetsArgvForNativeA2APath()
    {
        // BYOI conformance path 3: dapr-agent images speak A2A natively.
        // The launcher hands the dispatcher a non-empty argv so the
        // image's bridge ENTRYPOINT (if present) is bypassed and the
        // Python process boots directly. Matches the production CMD
        // declared by agents/spring-voyage-agent/Dockerfile.
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        prep.Argv.ShouldNotBeNull();
        prep.Argv.ShouldBe(new[] { "python", "agent.py" });
    }

    [Fact]
    public async Task PrepareAsync_SetsDaprAgentPortEnvVar()
    {
        // Issue #1097 introduces DAPR_AGENT_PORT as the contract name.
        // AGENT_PORT is kept alongside it for back-compat with the
        // existing in-container agent.py (PR 5 cuts the dispatcher
        // over).
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["DAPR_AGENT_PORT"].ShouldBe("8999");
        prep.EnvironmentVariables["AGENT_PORT"].ShouldBe("8999");
    }

    [Fact]
    public async Task PrepareAsync_LeavesStdinPayloadNull()
    {
        // dapr-agent reads requests over A2A, never via stdin.
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        prep.StdinPayload.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_DefaultsA2APortAndResponseCapture()
    {
        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

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
    public async Task PrepareAsync_AnthropicProvider_PropagatesAnthropicApiKey()
    {
        // ADR-0038: with `runtime: spring-voyage` and `model.provider: anthropic`
        // the launcher resolves the API key through ILlmCredentialResolver
        // and injects it as ANTHROPIC_API_KEY so the daprd sidecar's
        // local-env secret store can satisfy llm-anthropic.yaml's
        // secretKeyRef. The launcher also pins SPRING_LLM_COMPONENT to
        // llm-anthropic so agent.py dials the right Conversation
        // component instead of silently routing through Ollama.
        SeedTenantSecret("anthropic", "anthropic-api-key", "sk-ant-api-fake");
        var context = MakeContext("anthropic", "claude-sonnet-4-6");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_LLM_PROVIDER"].ShouldBe("anthropic");
        prep.EnvironmentVariables["SPRING_MODEL"].ShouldBe("claude-sonnet-4-6");
        prep.EnvironmentVariables["SPRING_LLM_COMPONENT"].ShouldBe("llm-anthropic");
        prep.EnvironmentVariables["ANTHROPIC_API_KEY"].ShouldBe("sk-ant-api-fake");
        // The Claude runtime's CredentialEnvVar is CLAUDE_CODE_OAUTH_TOKEN — that
        // env var name is intentionally for the CLI / agent-runtime path. The
        // Conversation REST path's secretKeyRef is ANTHROPIC_API_KEY (matching
        // ContainerLifecycleManager.CredentialEnvVarsToPropagate); injecting
        // under the runtime's CLI name would never reach the daprd sidecar.
        prep.EnvironmentVariables.ShouldNotContainKey("CLAUDE_CODE_OAUTH_TOKEN",
            "Spring Voyage REST path must inject ANTHROPIC_API_KEY, not CLAUDE_CODE_OAUTH_TOKEN.");
    }

    [Fact]
    public async Task PrepareAsync_UnknownProvider_ThrowsWithOperatorGuidance()
    {
        // Spring Voyage launcher must fail loudly when asked to dispatch via a
        // provider it can't map to a Conversation REST env var. The error
        // message names both the provider and the supported set so the operator
        // can fix the unit (or extend the launcher with a matching YAML).
        var context = MakeContext("acme", "acme-1");

        var ex = await Should.ThrowAsync<SpringException>(
            () => _launcher.PrepareAsync(context, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("acme");
        ex.Message.ShouldContain("anthropic, openai, google");
    }

    [Fact]
    public async Task PrepareAsync_OpenAiProvider_PropagatesOpenAiApiKey()
    {
        SeedTenantSecret("openai", "openai-api-key", "sk-openai-fake");
        var context = MakeContext("openai", "gpt-4o-mini");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_LLM_COMPONENT"].ShouldBe("llm-openai");
        prep.EnvironmentVariables["OPENAI_API_KEY"].ShouldBe("sk-openai-fake");
    }

    [Fact]
    public async Task PrepareAsync_GoogleProvider_PropagatesGoogleApiKey()
    {
        SeedTenantSecret("google", "google-api-key", "test-google-key");
        var context = MakeContext("google", "gemini-1.5-pro");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_LLM_COMPONENT"].ShouldBe("llm-google");
        prep.EnvironmentVariables["GOOGLE_API_KEY"].ShouldBe("test-google-key");
    }

    [Fact]
    public async Task PrepareAsync_OllamaProvider_DoesNotResolveCredential_AndPinsConversationOllama()
    {
        // Ollama is credential-less; the launcher must NOT consult the
        // resolver and must NOT inject any provider env var. It still
        // pins SPRING_LLM_COMPONENT so agent.py never falls back to a
        // stale default.
        var context = CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_LLM_COMPONENT"].ShouldBe("llm-ollama");
        prep.EnvironmentVariables.ShouldNotContainKey("ANTHROPIC_API_KEY");
        prep.EnvironmentVariables.ShouldNotContainKey("OPENAI_API_KEY");
        prep.EnvironmentVariables.ShouldNotContainKey("GOOGLE_API_KEY");
    }

    [Fact]
    public async Task PrepareAsync_MissingCredential_FailsDispatchWithGuidance()
    {
        // #1714 step 1: dispatch fails BEFORE container launch when no
        // value resolved at agent / unit / parent-unit chain / tenant scope.
        _credentialResolver
            .ResolveAsync("anthropic", Cvoya.Spring.Core.Catalog.AuthMethod.ApiKey, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCredentialResolution(
                Value: null,
                Source: LlmCredentialSource.NotFound,
                SecretName: "anthropic-api-key"));
        var context = MakeContext("anthropic", "claude-sonnet-4-6");

        var ex = await Should.ThrowAsync<SpringException>(
            () => _launcher.PrepareAsync(context, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("anthropic-api-key");
        ex.Message.ShouldContain("agent, unit, parent-unit chain, or tenant scope");
    }

    [Fact]
    public async Task PrepareAsync_OAuthOnSpringVoyageRest_FailsPreFlight()
    {
        // #1714 step 1: an OAuth token (sk-ant-oat…) belongs on the Claude
        // agent-runtime path, not the Spring Voyage REST path. The
        // launcher rejects pre-flight with operator guidance so the
        // dispatch never burns a network round-trip.
        SeedTenantSecret("anthropic", "anthropic-api-key", "sk-ant-oat-not-rest-shape");
        var context = MakeContext("anthropic", "claude-sonnet-4-6");

        var ex = await Should.ThrowAsync<SpringException>(
            () => _launcher.PrepareAsync(context, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("REST");
        ex.Message.ShouldContain("anthropic-api-key");
    }

    private static AgentLaunchContext MakeContext(string provider, string model) =>
        LauncherCallbackTestSupport.CreateContext(
            prompt: "## System\nYou are a helpful assistant.",
            mcpToken: "t",
            provider: provider,
            model: model);

    private static AgentLaunchContext CreateContext() =>
        LauncherCallbackTestSupport.CreateContext(
            prompt: "## System\nYou are a helpful assistant.",
            mcpToken: "test-token-xyz");
}