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
    public async Task PrepareAsync_CreatesWorkingDirectory()
    {
        var context = CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        try
        {
            Directory.Exists(prep.WorkingDirectory).ShouldBeTrue();
        }
        finally
        {
            await _launcher.CleanupAsync(prep.WorkingDirectory, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PrepareAsync_SetsRequiredEnvVars()
    {
        var context = CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        try
        {
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
        finally
        {
            await _launcher.CleanupAsync(prep.WorkingDirectory, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PrepareAsync_IncludesVolumeMount()
    {
        var context = CreateContext();

        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        try
        {
            prep.VolumeMounts.ShouldHaveSingleItem()
                .ShouldBe($"{prep.WorkingDirectory}:/workspace");
        }
        finally
        {
            await _launcher.CleanupAsync(prep.WorkingDirectory, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PrepareAsync_OmitsOllamaEndpoint_WhenBaseUrlIsNull()
    {
        var options = Options.Create(new OllamaOptions { DefaultModel = "phi3:mini", BaseUrl = "" });
        var launcher = new DaprAgentLauncher(options, _loggerFactory);
        var context = CreateContext();

        var prep = await launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        try
        {
            prep.EnvironmentVariables.ShouldNotContainKey("OLLAMA_ENDPOINT");
            prep.EnvironmentVariables["SPRING_MODEL"].ShouldBe("phi3:mini");
        }
        finally
        {
            await launcher.CleanupAsync(prep.WorkingDirectory, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CleanupAsync_DeletesWorkingDirectory()
    {
        var context = CreateContext();
        var prep = await _launcher.PrepareAsync(context, TestContext.Current.CancellationToken);

        await _launcher.CleanupAsync(prep.WorkingDirectory, TestContext.Current.CancellationToken);

        Directory.Exists(prep.WorkingDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task CleanupAsync_NonexistentDirectory_DoesNotThrow()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid());

        var act = () => _launcher.CleanupAsync(nonexistent, TestContext.Current.CancellationToken);

        await Should.NotThrowAsync(act);
    }

    private static AgentLaunchContext CreateContext() =>
        new(
            AgentId: "dapr-test-agent",
            ConversationId: "conv-99",
            Prompt: "## System\nYou are a helpful assistant.",
            McpEndpoint: "http://host.docker.internal:9999/mcp/",
            McpToken: "test-token-xyz");
}