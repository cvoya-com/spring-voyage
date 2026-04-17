// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using A2A;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

using A2AMessage = A2A.Message;
using SvMessage = Cvoya.Spring.Core.Messaging.Message;

/// <summary>
/// Unit tests for <see cref="A2AExecutionDispatcher"/>.
/// </summary>
public class A2AExecutionDispatcherTests
{
    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly IPromptAssembler _promptAssembler = Substitute.For<IPromptAssembler>();
    private readonly IAgentDefinitionProvider _agentProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IMcpServer _mcpServer = Substitute.For<IMcpServer>();
    private readonly IAgentToolLauncher _launcher = Substitute.For<IAgentToolLauncher>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IContainerRuntime _persistentContainerRuntime = Substitute.For<IContainerRuntime>();
    private readonly PersistentAgentRegistry _persistentRegistry;
    private readonly A2AExecutionDispatcher _dispatcher;
    private const string AgentId = "my-agent";
    private const string Image = "spring-agent-claude:v1";

    public A2AExecutionDispatcherTests()
    {
        _persistentRegistry = new PersistentAgentRegistry(
            _persistentContainerRuntime, _httpClientFactory, _loggerFactory);
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

        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        _dispatcher = new A2AExecutionDispatcher(
            _containerRuntime,
            _promptAssembler,
            _agentProvider,
            _mcpServer,
            [_launcher],
            _persistentRegistry,
            _httpClientFactory,
            _loggerFactory);
    }

    private static SvMessage CreateMessage(
        string toPath = AgentId,
        string? conversationId = null)
    {
        return new SvMessage(
            Guid.NewGuid(),
            new Address("agent", "sender"),
            new Address("agent", toPath),
            MessageType.Domain,
            conversationId ?? Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Task = "do-work" }),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_CallsContainerRuntime()
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
    public async Task DispatchAsync_EphemeralAgent_UsesImageFromAgentDefinition()
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
    public async Task DispatchAsync_ForwardsProviderAndModelFromAgentDefinitionToLaunchContext()
    {
        // #480 step 5: providers other than Ollama must be reachable via a
        // YAML-only change on the AgentDefinition. The dispatcher reads
        // execution.provider / execution.model and forwards them through the
        // AgentLaunchContext so the launcher can pin the Conversation
        // component by name. No live provider call needed — we verify the
        // wiring by inspecting the context the dispatcher hands to the stub
        // launcher.
        var message = CreateMessage();
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: AgentId,
                Name: "My Agent",
                Instructions: null,
                Execution: new AgentExecutionConfig(
                    Tool: "claude-code",
                    Image: Image,
                    Provider: "openai",
                    Model: "gpt-4o-mini")));
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-provider", 0, "", ""));

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        await _launcher.Received(1).PrepareAsync(
            Arg.Is<AgentLaunchContext>(ctx =>
                ctx.Provider == "openai" &&
                ctx.Model == "gpt-4o-mini"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_IssuesMcpSessionAndPassesToLauncher()
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
    public async Task DispatchAsync_EphemeralAgent_CleansUpAndRevokesSession_OnSuccess()
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
    public async Task DispatchAsync_EphemeralAgent_CleansUpAndRevokesSession_OnFailure()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("runtime boom"));

        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(act);

        _mcpServer.Received(1).RevokeSession("test-token");
        await _launcher.Received(1).CleanupAsync("/tmp/test-workdir", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_ContainerSucceeds_ReturnsResponseMessage()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("test prompt");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-456", 0, "success output", ""));

        var result = await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.From.ShouldBe(message.To);
        result.To.ShouldBe(message.From);
        result.ConversationId.ShouldBe(message.ConversationId);
        result.Type.ShouldBe(MessageType.Domain);

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Output").GetString().ShouldBe("success output");
        payload.GetProperty("ExitCode").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_ContainerFails_ReturnsErrorMessage()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-789", 1, "", "error occurred"));

        var result = await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("ExitCode").GetInt32().ShouldBe(1);
        payload.GetProperty("Error").GetString().ShouldBe("error occurred");
    }

    [Fact]
    public async Task DispatchAsync_UnknownAgent_Throws()
    {
        var message = CreateMessage(toPath: "ghost");
        _agentProvider.GetByIdAsync("ghost", Arg.Any<CancellationToken>())
            .Returns((AgentDefinition?)null);

        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<SpringException>(act);
        ex.Message.ShouldContain("No agent definition");
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
        var ex = await Should.ThrowAsync<SpringException>(act);
        ex.Message.ShouldContain("No IAgentToolLauncher");
    }

    [Fact]
    public async Task DispatchAsync_PersistentAgent_NotRunning_AttemptsAutoStart()
    {
        var message = CreateMessage();
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId, "My Agent", "instructions",
                new AgentExecutionConfig("claude-code", Image, Hosting: AgentHostingMode.Persistent)));
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");

        // StartAsync returns a container ID, but readiness probe will fail (no real server)
        // so we expect the dispatch to fail. Use a short cancellation timeout to avoid
        // waiting the full 60-second readiness timeout.
        _containerRuntime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns("spring-persistent-abc");
        _httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        // The dispatcher will attempt auto-start, the readiness probe will fail,
        // and cancellation will cut the wait short.
        var act = () => _dispatcher.DispatchAsync(message, context: null, cts.Token);
        await Should.ThrowAsync<Exception>(act);

        // Verify StartAsync was called on the main container runtime (auto-start attempted).
        await _containerRuntime.Received(1).StartAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_NullImage_Throws()
    {
        var message = CreateMessage();
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId, "My Agent", null,
                new AgentExecutionConfig("claude-code", Image: null)));
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");

        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<SpringException>(act);
        ex.Message.ShouldContain("requires a container image");
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

    [Fact]
    public void MapA2AResponseToMessage_TaskCompleted_ReturnsSuccessPayload()
    {
        var originalMessage = CreateMessage();
        var response = new SendMessageResponse
        {
            Task = new AgentTask
            {
                Id = Guid.NewGuid().ToString(),
                Status = new A2A.TaskStatus
                {
                    State = TaskState.Completed,
                },
                Artifacts = [new Artifact
                {
                    ArtifactId = Guid.NewGuid().ToString(),
                    Parts = [new Part { Text = "agent output" }],
                }],
            },
        };

        var result = A2AExecutionDispatcher.MapA2AResponseToMessage(originalMessage, response);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Output").GetString().ShouldBe("agent output");
        payload.GetProperty("ExitCode").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void MapA2AResponseToMessage_TaskFailed_ReturnsErrorPayload()
    {
        var originalMessage = CreateMessage();
        var response = new SendMessageResponse
        {
            Task = new AgentTask
            {
                Id = Guid.NewGuid().ToString(),
                Status = new A2A.TaskStatus
                {
                    State = TaskState.Failed,
                },
            },
        };

        var result = A2AExecutionDispatcher.MapA2AResponseToMessage(originalMessage, response);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("ExitCode").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void MapA2AResponseToMessage_MessageResponse_ReturnsTextOutput()
    {
        var originalMessage = CreateMessage();
        var response = new SendMessageResponse
        {
            Message = new A2AMessage
            {
                Role = Role.Agent,
                Parts = [new Part { Text = "direct reply" }],
            },
        };

        var result = A2AExecutionDispatcher.MapA2AResponseToMessage(originalMessage, response);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Output").GetString().ShouldBe("direct reply");
        payload.GetProperty("ExitCode").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void MapA2AResponseToMessage_PreservesMessageRouting()
    {
        var originalMessage = CreateMessage();
        var response = new SendMessageResponse
        {
            Message = new A2AMessage
            {
                Role = Role.Agent,
                Parts = [new Part { Text = "ok" }],
            },
        };

        var result = A2AExecutionDispatcher.MapA2AResponseToMessage(originalMessage, response);

        result.ShouldNotBeNull();
        result!.From.ShouldBe(originalMessage.To);
        result.To.ShouldBe(originalMessage.From);
        result.ConversationId.ShouldBe(originalMessage.ConversationId);
        result.Type.ShouldBe(MessageType.Domain);
    }

    [Fact]
    public async Task DispatchAsync_DefaultHostingMode_IsEphemeral()
    {
        // Ensure that AgentExecutionConfig with no explicit hosting defaults to Ephemeral
        var config = new AgentExecutionConfig("claude-code", Image);
        config.Hosting.ShouldBe(AgentHostingMode.Ephemeral);

        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("c1", 0, "ok", ""));

        // Should route to ephemeral path and call container runtime
        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        await _containerRuntime.Received(1).RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }
}