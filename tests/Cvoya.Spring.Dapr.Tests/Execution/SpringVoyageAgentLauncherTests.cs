// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

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
        _launcher = new SpringVoyageAgentLauncher(_ollamaOptions, _loggerFactory);
    }

    [Fact]
    public void ToolKind_IsSpringVoyage()
    {
        // #1732: launcher key matches IAgentRuntime.ToolKind. Multiple
        // runtimes (openai/google/ollama) share spring-voyage.
        _launcher.ToolKind.ShouldBe("spring-voyage");
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
        // #1327: SPRING_MODEL and SPRING_LLM_PROVIDER are now D1-spec-declared (§ 2.2.1).
        prep.EnvironmentVariables["SPRING_MODEL"].ShouldBe("llama3.2:3b");
        prep.EnvironmentVariables["SPRING_LLM_PROVIDER"].ShouldBe("ollama");
        prep.EnvironmentVariables["AGENT_PORT"].ShouldBe("8999");
        // #1328: OLLAMA_ENDPOINT removed from launcher; conversation-ollama.yaml now reads SPRING_LLM_PROVIDER_URL.
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
        var launcher = new SpringVoyageAgentLauncher(options, _loggerFactory);
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
        var context = new AgentLaunchContext(
            AgentId: "dapr-test-agent",
            ThreadId: "conv-openai",
            Prompt: "prompt",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "t",
            TenantId: Cvoya.Spring.Core.Tenancy.OssTenantIds.Default,
            Provider: "openai",
            Model: "gpt-4o-mini");

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

        prep.EnvironmentVariables.ShouldContainKey(AgentVolumeManager.WorkspacePathEnvVar);
        prep.EnvironmentVariables[AgentVolumeManager.WorkspacePathEnvVar]
            .ShouldBe(AgentVolumeManager.WorkspaceMountPath);
    }

    [Fact]
    public async Task PrepareAsync_AnthropicProvider_DoesNotPropagateAnthropicApiKey_TrackedByFollowUp()
    {
        // #1690 + #1714: when an operator configures
        //   `agent: spring-voyage, provider: anthropic, model: <m>`
        // the credential matrix says ANTHROPIC_API_KEY should land on
        // the agent container's env so agent.py's DaprChatClient (or
        // any direct Anthropic SDK call) can authenticate. Today the
        // launcher does not propagate the credential — neither
        // SpringVoyageAgentLauncher nor AgentContextBuilder injects it, and the
        // OSS deploy ships only conversation-ollama.yaml so the Dapr
        // Conversation component the Python agent dials is wired to
        // Ollama regardless of `provider`.
        //
        // This test pins the gap so #1714's fix lands as a single
        // delete-this-assertion + add-the-positive-assertion diff.
        var context = new AgentLaunchContext(
            AgentId: "dapr-anthropic-agent",
            ThreadId: "conv-anthropic-1",
            Prompt: "## System\nYou are a helpful assistant.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "t",
            TenantId: Cvoya.Spring.Core.Tenancy.OssTenantIds.Default,
            Provider: "anthropic",
            Model: "claude-sonnet-4-6");

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_LLM_PROVIDER"].ShouldBe("anthropic");
        prep.EnvironmentVariables["SPRING_MODEL"].ShouldBe("claude-sonnet-4-6");
        // GAP: the launcher does not yet propagate the Anthropic credential.
        // Once #1714 wires per-provider Conversation components and credential
        // injection, flip this to a positive assertion that ANTHROPIC_API_KEY
        // is set to the resolved slot value.
        prep.EnvironmentVariables.ShouldNotContainKey(
            "ANTHROPIC_API_KEY",
            "ANTHROPIC_API_KEY propagation for spring-voyage + Anthropic is tracked by #1714 — " +
            "flip this assertion when that issue lands.");
    }

    private static AgentLaunchContext CreateContext() =>
        new(
            AgentId: "dapr-test-agent",
            ThreadId: "conv-99",
            Prompt: "## System\nYou are a helpful assistant.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "test-token-xyz",
            TenantId: Cvoya.Spring.Core.Tenancy.OssTenantIds.Default);
}