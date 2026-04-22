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
/// Unit tests for <see cref="DaprAgentLauncher"/>.
/// </summary>
public class DaprAgentLauncherTests
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<OllamaOptions> _ollamaOptions;
    private readonly DaprAgentLauncher _launcher;

    public DaprAgentLauncherTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _ollamaOptions = Options.Create(new OllamaOptions
        {
            DefaultModel = "llama3.2:3b",
            BaseUrl = "http://spring-ollama:11434",
        });
        _launcher = new DaprAgentLauncher(_ollamaOptions, _loggerFactory);
    }

    [Fact]
    public void Tool_IsDaprAgent()
    {
        _launcher.Tool.ShouldBe("dapr-agent");
    }

    [Fact]
    public async Task PrepareAsync_DoesNotTouchLocalFilesystem()
    {
        // Issue #1042: launchers must no longer materialise workspace dirs on
        // the worker side — the dispatcher owns that. Verify by snapshotting
        // the temp dir before/after.
        var preExisting = new HashSet<string>(Directory.EnumerateFileSystemEntries(Path.GetTempPath()));

        var prep = await _launcher.PrepareAsync(CreateContext(), TestContext.Current.CancellationToken);

        var postExisting = Directory.EnumerateFileSystemEntries(Path.GetTempPath());
        postExisting.Where(p => !preExisting.Contains(p))
            .ShouldBeEmpty("DaprAgentLauncher must not touch the local filesystem");

        prep.WorkspaceMountPath.ShouldBe("/workspace");
    }

    [Fact]
    public async Task PrepareAsync_SetsRequiredEnvVars()
    {
        var context = CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables["SPRING_AGENT_ID"].ShouldBe(context.AgentId);
        prep.EnvironmentVariables["SPRING_CONVERSATION_ID"].ShouldBe(context.ConversationId);
        prep.EnvironmentVariables["SPRING_MCP_ENDPOINT"].ShouldBe(context.McpEndpoint);
        prep.EnvironmentVariables["SPRING_AGENT_TOKEN"].ShouldBe(context.McpToken);
        prep.EnvironmentVariables["SPRING_SYSTEM_PROMPT"].ShouldBe(context.Prompt);
        prep.EnvironmentVariables["SPRING_MODEL"].ShouldBe("llama3.2:3b");
        prep.EnvironmentVariables["SPRING_LLM_PROVIDER"].ShouldBe("ollama");
        prep.EnvironmentVariables["AGENT_PORT"].ShouldBe("8999");
        prep.EnvironmentVariables["OLLAMA_ENDPOINT"].ShouldBe("http://spring-ollama:11434");
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
    public async Task PrepareAsync_OmitsOllamaEndpoint_WhenBaseUrlIsNull()
    {
        var options = Options.Create(new OllamaOptions { DefaultModel = "phi3:mini", BaseUrl = "" });
        var launcher = new DaprAgentLauncher(options, _loggerFactory);
        var context = CreateContext();

        var prep = await launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        prep.EnvironmentVariables.ShouldNotContainKey("OLLAMA_ENDPOINT");
        prep.EnvironmentVariables["SPRING_MODEL"].ShouldBe("phi3:mini");
    }

    [Fact]
    public async Task PrepareAsync_UsesProviderAndModelFromLaunchContext_WhenProvided()
    {
        // #480 step 5: when the AgentDefinition specifies a provider/model,
        // DaprAgentLauncher must forward them to the container env vars so the
        // Python Dapr Agent binds to the matching Conversation component.
        var context = new AgentLaunchContext(
            AgentId: "dapr-test-agent",
            ConversationId: "conv-openai",
            Prompt: "prompt",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "t",
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

    private static AgentLaunchContext CreateContext() =>
        new(
            AgentId: "dapr-test-agent",
            ConversationId: "conv-99",
            Prompt: "## System\nYou are a helpful assistant.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "test-token-xyz");
}