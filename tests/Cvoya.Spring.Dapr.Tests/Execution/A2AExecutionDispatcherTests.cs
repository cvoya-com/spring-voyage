// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
///
/// PR 5 of the #1087 series collapsed ephemeral and persistent dispatch onto
/// the same A2A path. These tests exercise the new flow:
/// <see cref="ContainerConfigBuilder"/> builds the config, the dispatcher
/// starts the container in detached mode, waits for A2A readiness, sends the
/// message via A2A, and tears the ephemeral container down on completion.
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
    private readonly EphemeralAgentRegistry _ephemeralRegistry;
    private readonly A2AExecutionDispatcher _dispatcher;
    private const string AgentId = "my-agent";
    private const string Image = "spring-agent-claude:v1";
    private const string ContainerId = "spring-ephemeral-abc";

    private static readonly AgentLaunchSpec DefaultSpec = new(
        WorkspaceFiles: new Dictionary<string, string> { ["CLAUDE.md"] = "prepared" },
        EnvironmentVariables: new Dictionary<string, string> { ["SPRING_SYSTEM_PROMPT"] = "prepared" },
        WorkspaceMountPath: "/workspace");

    public A2AExecutionDispatcherTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _persistentRegistry = new PersistentAgentRegistry(
            _persistentContainerRuntime, _httpClientFactory, _loggerFactory);
        _ephemeralRegistry = new EphemeralAgentRegistry(
            _containerRuntime, _loggerFactory);
        _launcher.Tool.Returns("claude-code");
        _launcher.PrepareAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .Returns(DefaultSpec);

        _mcpServer.Endpoint.Returns("http://host.docker.internal:12345/mcp/");
        _mcpServer.IssueSession(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new McpSession("test-token", ci.ArgAt<string>(0), ci.ArgAt<string>(1)));

        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: AgentId,
                Name: "My Agent",
                Instructions: "do things",
                Execution: new AgentExecutionConfig("claude-code", Image)));

        // Default: container starts and the readiness probe will fail (no real
        // server) so the dispatch fails cleanly with a SpringException. Tests
        // that need the full A2A roundtrip swap in a stub HttpClient that
        // answers 200 on /.well-known/agent.json.
        _containerRuntime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(ContainerId);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        _dispatcher = new A2AExecutionDispatcher(
            _containerRuntime,
            _promptAssembler,
            _agentProvider,
            _mcpServer,
            [_launcher],
            _persistentRegistry,
            _ephemeralRegistry,
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

    /// <summary>
    /// Wires the http client factory so the readiness probe and the A2A
    /// SendMessage call see a stub responder. Returns the responder so tests
    /// can inspect the requests it received.
    /// </summary>
    private StubA2AResponder InstallA2AStub(string responseText = "agent reply")
    {
        var responder = new StubA2AResponder(responseText);
        _httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(responder, disposeHandler: false));
        return responder;
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_StartsContainerInDetachedMode()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("assembled prompt");
        InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        // PR 5 of #1087: ephemeral dispatch no longer goes through RunAsync;
        // it starts the container detached, talks to it over A2A, and tears
        // it down via the EphemeralAgentRegistry.
        await _containerRuntime.Received(1).StartAsync(
            Arg.Any<ContainerConfig>(),
            Arg.Any<CancellationToken>());
        await _containerRuntime.DidNotReceive().RunAsync(
            Arg.Any<ContainerConfig>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_BuildsContainerConfigViaContainerConfigBuilder()
    {
        // Issue #1042 + #1094: the dispatcher must hand the runtime exactly
        // what the shared ContainerConfigBuilder would produce from the
        // launcher's spec — no inline duplication of the construction.
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");
        InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        var expected = ContainerConfigBuilder.Build(Image, DefaultSpec);
        await _containerRuntime.Received(1).StartAsync(
            Arg.Is<ContainerConfig>(c =>
                c.Image == expected.Image &&
                c.Workspace != null &&
                c.Workspace.MountPath == expected.Workspace!.MountPath &&
                c.Workspace.Files.ContainsKey("CLAUDE.md") &&
                c.WorkingDirectory == expected.WorkingDirectory),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_UsesImageFromAgentDefinition()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("assembled prompt");
        InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).StartAsync(
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
        // component by name.
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
        InstallA2AStub();

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
        InstallA2AStub();

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
    public async Task DispatchAsync_EphemeralAgent_RevokesSessionAndStopsContainer_OnSuccess()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");
        InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        _mcpServer.Received(1).RevokeSession("test-token");
        // The ephemeral path tears the container down via the registry, which
        // delegates to IContainerRuntime.StopAsync.
        await _containerRuntime.Received(1).StopAsync(ContainerId, Arg.Any<CancellationToken>());
        _ephemeralRegistry.GetAllEntries().ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_RevokesSessionAndStopsContainer_OnFailure()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");
        // No A2A stub — readiness probe will fail. Container was started so
        // it must still be torn down.
        _containerRuntime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(ContainerId);

        // Use a tight readiness budget via cancellation so we don't wait the
        // full 60-second probe budget.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        var act = () => _dispatcher.DispatchAsync(message, context: null, cts.Token);
        await Should.ThrowAsync<Exception>(act);

        _mcpServer.Received(1).RevokeSession("test-token");
        await _containerRuntime.Received(1).StopAsync(ContainerId, Arg.Any<CancellationToken>());
        _ephemeralRegistry.GetAllEntries().ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_StartFails_StillRevokesSession()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");
        _containerRuntime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("runtime boom"));

        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(act);

        _mcpServer.Received(1).RevokeSession("test-token");
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_A2ARoundtrip_ReturnsResponseTextInPayload()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("the prompt");
        InstallA2AStub("hello from agent");

        var result = await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.From.ShouldBe(message.To);
        result.To.ShouldBe(message.From);
        result.ConversationId.ShouldBe(message.ConversationId);
        result.Type.ShouldBe(MessageType.Domain);

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Output").GetString().ShouldBe("hello from agent");
        payload.GetProperty("ExitCode").GetInt32().ShouldBe(0);
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        var act = () => _dispatcher.DispatchAsync(message, context: null, cts.Token);
        await Should.ThrowAsync<Exception>(act);

        await _containerRuntime.Received(1).StartAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_PersistentAgent_BuildsContainerConfigViaContainerConfigBuilder()
    {
        // The persistent path also flows through ContainerConfigBuilder so the
        // two dispatch modes can't drift on what a container looks like.
        var message = CreateMessage();
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId, "My Agent", "instructions",
                new AgentExecutionConfig("claude-code", Image, Hosting: AgentHostingMode.Persistent)));
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");
        _containerRuntime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns("spring-persistent-cc");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        try { await _dispatcher.DispatchAsync(message, context: null, cts.Token); }
        catch { /* readiness probe will fail; assertion on the StartAsync call is what we want */ }

        var expected = ContainerConfigBuilder.Build(Image, DefaultSpec);
        await _containerRuntime.Received(1).StartAsync(
            Arg.Is<ContainerConfig>(c =>
                c.Image == expected.Image &&
                c.Workspace != null &&
                c.Workspace.MountPath == expected.Workspace!.MountPath),
            Arg.Any<CancellationToken>());
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
    public async Task DispatchAsync_PooledHosting_ThrowsNotSupported()
    {
        // PR 1 of #1087: Pooled is reserved on the enum for #362 but not
        // implemented yet. The dispatcher must reject the value explicitly
        // so it can't silently fall through to the ephemeral path. PR 5
        // must preserve this guard.
        var message = CreateMessage();
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId, "My Agent", "instructions",
                new AgentExecutionConfig("claude-code", Image, Hosting: AgentHostingMode.Pooled)));
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");

        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<NotSupportedException>(act);
        ex.Message.ShouldContain("#362");
    }

    [Fact]
    public async Task DispatchAsync_PassesPromptAsEnvironmentVariable()
    {
        var message = CreateMessage();
        var expectedPrompt = "the assembled prompt";

        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(expectedPrompt);
        InstallA2AStub();

        _launcher.PrepareAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AgentLaunchSpec(
                WorkspaceFiles: new Dictionary<string, string>(),
                EnvironmentVariables: new Dictionary<string, string>
                {
                    ["SPRING_SYSTEM_PROMPT"] = ci.ArgAt<AgentLaunchContext>(0).Prompt
                },
                WorkspaceMountPath: "/workspace"));

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).StartAsync(
            Arg.Is<ContainerConfig>(c =>
                c.EnvironmentVariables != null &&
                c.EnvironmentVariables.ContainsKey("SPRING_SYSTEM_PROMPT") &&
                c.EnvironmentVariables["SPRING_SYSTEM_PROMPT"] == expectedPrompt),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_Cancelled_TearsDownContainer()
    {
        // PR 5 of #1087: when the conversation is cancelled mid-turn the
        // ephemeral container must still be torn down — the registry holds
        // the lease and the dispatcher's finally block releases it on the
        // way out (with CancellationToken.None so the teardown itself is
        // not cancelled by the same token that triggered the cancel).
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");

        // Build a stub that blocks on SendMessage so we can fire the cancel.
        var responder = new BlockingA2AResponder();
        _httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(responder, disposeHandler: false));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var dispatchTask = _dispatcher.DispatchAsync(message, context: null, cts.Token);

        // Wait until the readiness probe + SendMessage call has been issued,
        // then cancel.
        await responder.WaitForSendMessageAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        try { await dispatchTask; } catch { /* expected — cancelled */ }

        // Container teardown should fire exactly once via the registry, even
        // though the caller's token was cancelled.
        await _containerRuntime.Received(1).StopAsync(ContainerId, Arg.Any<CancellationToken>());
        _ephemeralRegistry.GetAllEntries().ShouldBeEmpty();
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
        InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        await _containerRuntime.Received(1).StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }
}

/// <summary>
/// HttpMessageHandler that answers any GET <c>/.well-known/agent.json</c>
/// request with 200 and any A2A <c>message/send</c> JSON-RPC POST with a
/// completed task whose artifact carries the configured response text.
/// </summary>
internal sealed class StubA2AResponder(string responseText) : HttpMessageHandler
{
    private readonly string _responseText = responseText;

    public int ReadinessProbes { get; private set; }
    public int SendMessageCalls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/.well-known/agent.json") == true)
        {
            ReadinessProbes++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"name\":\"stub\"}"),
            };
        }

        if (request.Method == HttpMethod.Post)
        {
            SendMessageCalls++;
            // SendMessageResponse on the wire is field-presence driven —
            // either `task` or `message` is set on `result`. The bridge
            // returns a Task because every CLI invocation is a task in the
            // bridge's model (see deployment/agent-sidecar/src/a2a.ts).
            var body = $$"""
                {
                  "jsonrpc": "2.0",
                  "id": 1,
                  "result": {
                    "task": {
                      "id": "task-1",
                      "contextId": "ctx",
                      "status": { "state": "TASK_STATE_COMPLETED" },
                      "artifacts": [
                        {
                          "artifactId": "a-1",
                          "parts": [ { "kind": "text", "text": "{{System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(_responseText)}}" } ]
                        }
                      ]
                    }
                  }
                }
                """;
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// Like <see cref="StubA2AResponder"/> but the message/send POST blocks until
/// the cancellation token fires. Used by the cancellation test to ensure the
/// dispatcher is mid-flight when we cancel.
/// </summary>
internal sealed class BlockingA2AResponder : HttpMessageHandler
{
    private readonly TaskCompletionSource<bool> _sendStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForSendMessageAsync(TimeSpan timeout)
    {
        return Task.WhenAny(_sendStarted.Task, Task.Delay(timeout))
            .ContinueWith(t =>
            {
                if (!_sendStarted.Task.IsCompleted)
                {
                    throw new TimeoutException("SendMessage was not invoked within timeout.");
                }
            });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/.well-known/agent.json") == true)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"name\":\"stub\"}"),
            };
        }

        if (request.Method == HttpMethod.Post)
        {
            _sendStarted.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}