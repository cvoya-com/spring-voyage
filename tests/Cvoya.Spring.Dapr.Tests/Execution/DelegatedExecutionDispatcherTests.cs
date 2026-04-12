// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Execution;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DelegatedExecutionDispatcher"/>.
/// </summary>
public class DelegatedExecutionDispatcherTests
{
    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly IPromptAssembler _promptAssembler = Substitute.For<IPromptAssembler>();
    private readonly IAgentDefinitionProvider _agentProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IMcpServer _mcpServer = Substitute.For<IMcpServer>();
    private readonly IAgentToolLauncher _launcher = Substitute.For<IAgentToolLauncher>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly DelegatedExecutionDispatcher _dispatcher;
    private const string AgentId = "my-agent";
    private const string Image = "spring-agent-claude:v1";

    public DelegatedExecutionDispatcherTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _launcher.Tool.Returns("claude-code");
        _launcher.PrepareAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentLaunchPrep(
                WorkingDirectory: "/tmp/test-workdir",
                EnvironmentVariables: new Dictionary<string, string> { ["SPRING_SYSTEM_PROMPT"] = "prepared" },
                VolumeMounts: ["/tmp/test-workdir:/workspace"]));
        _launcher.CleanupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _mcpServer.Endpoint.Returns("http://host.docker.internal:12345/mcp/");
        _mcpServer.IssueSession(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new McpSession("test-token", ci.ArgAt<string>(0), ci.ArgAt<string>(1)));

        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: AgentId,
                Name: "My Agent",
                Instructions: "do things",
                Execution: new AgentExecutionConfig("claude-code", Image)));

        _dispatcher = new DelegatedExecutionDispatcher(
            _containerRuntime,
            _promptAssembler,
            _agentProvider,
            _mcpServer,
            [_launcher],
            _loggerFactory);
    }

    private static Message CreateMessage(
        string toPath = AgentId,
        string? conversationId = null)
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", "sender"),
            new Address("agent", toPath),
            MessageType.Domain,
            conversationId ?? Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Task = "do-work" }),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task DispatchAsync_CallsContainerRuntime()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("assembled prompt");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-123", 0, "output", ""));

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).RunAsync(
            Arg.Any<ContainerConfig>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_UsesImageFromAgentDefinition()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("assembled prompt");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-img", 0, "", ""));

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).RunAsync(
            Arg.Is<ContainerConfig>(c => c.Image == Image),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_IssuesMcpSessionAndPassesToLauncher()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("the prompt");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-mcp", 0, "", ""));

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        _mcpServer.Received(1).IssueSession(AgentId, message.ConversationId!);
        await _launcher.Received(1).PrepareAsync(
            Arg.Is<AgentLaunchContext>(ctx =>
                ctx.AgentId == AgentId &&
                ctx.ConversationId == message.ConversationId &&
                ctx.McpToken == "test-token" &&
                ctx.McpEndpoint == "http://host.docker.internal:12345/mcp/" &&
                ctx.Prompt == "the prompt"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_CleansUpAndRevokesSession_OnSuccess()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec", 0, "", ""));

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        _mcpServer.Received(1).RevokeSession("test-token");
        await _launcher.Received(1).CleanupAsync("/tmp/test-workdir", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_CleansUpAndRevokesSession_OnFailure()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("runtime boom"));

        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();

        _mcpServer.Received(1).RevokeSession("test-token");
        await _launcher.Received(1).CleanupAsync("/tmp/test-workdir", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_ContainerSucceeds_ReturnsResponseMessage()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("test prompt");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-456", 0, "success output", ""));

        var result = await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.From.Should().Be(message.To);
        result.To.Should().Be(message.From);
        result.ConversationId.Should().Be(message.ConversationId);
        result.Type.Should().Be(MessageType.Domain);

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Output").GetString().Should().Be("success output");
        payload.GetProperty("ExitCode").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_ContainerFails_ReturnsErrorMessage()
    {
        var message = CreateMessage();

        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-789", 1, "", "error occurred"));

        var result = await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("ExitCode").GetInt32().Should().Be(1);
        payload.GetProperty("Error").GetString().Should().Be("error occurred");
    }

    [Fact]
    public async Task DispatchAsync_UnknownAgent_Throws()
    {
        var message = CreateMessage(toPath: "ghost");
        _agentProvider.GetByIdAsync("ghost", Arg.Any<CancellationToken>())
            .Returns((AgentDefinition?)null);

        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<SpringException>().WithMessage("*No agent definition*");
    }

    [Fact]
    public async Task DispatchAsync_UnknownTool_Throws()
    {
        var message = CreateMessage();
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId, "My Agent", null,
                new AgentExecutionConfig("codex", Image)));

        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<SpringException>().WithMessage("*No IAgentToolLauncher*");
    }

    [Fact]
    public async Task DispatchAsync_PassesPromptAsEnvironmentVariable()
    {
        var message = CreateMessage();
        var expectedPrompt = "the assembled prompt";

        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(expectedPrompt);
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-env", 0, "output", ""));

        // Have the launcher echo the prompt through as an env var so we can assert it at the container layer.
        _launcher.PrepareAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AgentLaunchPrep(
                WorkingDirectory: "/tmp/test-workdir",
                EnvironmentVariables: new Dictionary<string, string>
                {
                    ["SPRING_SYSTEM_PROMPT"] = ci.ArgAt<AgentLaunchContext>(0).Prompt
                },
                VolumeMounts: []));

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).RunAsync(
            Arg.Is<ContainerConfig>(c =>
                c.EnvironmentVariables != null &&
                c.EnvironmentVariables.ContainsKey("SPRING_SYSTEM_PROMPT") &&
                c.EnvironmentVariables["SPRING_SYSTEM_PROMPT"] == expectedPrompt),
            Arg.Any<CancellationToken>());
    }
}