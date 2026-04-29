// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using A2A.V0_3;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

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

        var daprEph = Substitute.For<IDaprSidecarManager>();
        var daprOptions = new DaprSidecarOptions();
        var clmEph = new ContainerLifecycleManager(
            _containerRuntime, daprEph, Options.Create(daprOptions), _loggerFactory);
        _ephemeralRegistry = new EphemeralAgentRegistry(
            _containerRuntime, clmEph, _loggerFactory);

        var persistentServices = new ServiceCollection();
        persistentServices.AddSingleton(_persistentContainerRuntime);
        persistentServices.AddSingleton(_httpClientFactory);
        persistentServices.AddSingleton(_loggerFactory);
        persistentServices.AddSingleton(Substitute.For<IDaprSidecarManager>());
        persistentServices.AddSingleton(Options.Create(daprOptions));
        persistentServices.AddSingleton<ContainerLifecycleManager>();
        persistentServices.AddSingleton(Substitute.For<IAgentDefinitionProvider>());
        persistentServices.AddSingleton(Substitute.For<IMcpServer>());
        persistentServices.AddSingleton(_launcher);
        persistentServices.AddSingleton<IEnumerable<IAgentToolLauncher>>(
            p => [p.GetRequiredService<IAgentToolLauncher>()]);
        persistentServices.AddSingleton<PersistentAgentRegistry>();
        persistentServices.AddSingleton<PersistentAgentLifecycle>();
        _persistentRegistry = persistentServices
            .BuildServiceProvider()
            .GetRequiredService<PersistentAgentRegistry>();

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

        var daprD = Substitute.For<IDaprSidecarManager>();
        var clmD = new ContainerLifecycleManager(
            _containerRuntime, daprD, Options.Create(daprOptions), _loggerFactory);

        _dispatcher = new A2AExecutionDispatcher(
            _containerRuntime,
            _promptAssembler,
            _agentProvider,
            _mcpServer,
            [_launcher],
            _persistentRegistry,
            _ephemeralRegistry,
            clmD,
            Options.Create(daprOptions),
            _loggerFactory);
    }

    private static SvMessage CreateMessage(
        string toPath = AgentId,
        string? threadId = null)
    {
        return new SvMessage(
            Guid.NewGuid(),
            new Address("agent", "sender"),
            new Address("agent", toPath),
            MessageType.Domain,
            threadId ?? Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Task = "do-work" }),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Wires the container runtime so both halves of the A2A roundtrip — the
    /// readiness probe AND the JSON-RPC <c>message/send</c> POST — answer
    /// successfully. Both legs go through <see cref="IContainerRuntime"/>
    /// since #1160 closed: <see cref="IContainerRuntime.ProbeContainerHttpAsync"/>
    /// covers readiness and <see cref="IContainerRuntime.SendHttpJsonAsync"/>
    /// covers the message-send call (the worker no longer talks HTTP directly
    /// to the agent container). The returned recorder lets tests assert on the
    /// proxied POST payloads the dispatcher would have shipped on the wire.
    /// </summary>
    private SendHttpJsonRecorder InstallA2AStub(string responseText = "agent reply")
    {
        var recorder = new SendHttpJsonRecorder(responseText);
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _containerRuntime.SendHttpJsonAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(call => recorder.RespondAsync(
                call.ArgAt<string>(0),
                call.ArgAt<string>(1),
                call.ArgAt<byte[]>(2),
                call.ArgAt<CancellationToken>(3)));
        return recorder;
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

        _mcpServer.Received(1).IssueSession(AgentId, message.ThreadId!);
        await _launcher.Received(1).PrepareAsync(
            Arg.Is<AgentLaunchContext>(ctx =>
                ctx.AgentId == AgentId &&
                ctx.ThreadId == message.ThreadId &&
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
    public async Task DispatchAsync_EphemeralAgent_ReadinessProbe_DispatchedThroughContainerRuntime()
    {
        // #1160: the readiness probe must go through
        // IContainerRuntime.ProbeContainerHttpAsync so it runs inside the
        // agent container's network namespace. Probing via HttpClient from
        // the worker process won't work since #1063 — the worker can't
        // reach the agent container's loopback.
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");
        InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        await _containerRuntime.Received().ProbeContainerHttpAsync(
            ContainerId,
            Arg.Is<string>(url => url.EndsWith("/.well-known/agent.json")),
            Arg.Any<CancellationToken>());
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
        result.ThreadId.ShouldBe(message.ThreadId);
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

        // Both legs of the A2A roundtrip now go through IContainerRuntime
        // (see #1160). Readiness answers healthy immediately so the
        // dispatcher proceeds to the JSON-RPC POST, which we hold open via
        // SendHttpJsonAsync until the test fires the cancel.
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        var sendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _containerRuntime.SendHttpJsonAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                sendStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(3));
                return new ContainerHttpResponse(200, []);
            });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var dispatchTask = _dispatcher.DispatchAsync(message, context: null, cts.Token);

        // Wait until the readiness probe + SendMessage call has been issued,
        // then cancel.
        var ready = await Task.WhenAny(
            sendStarted.Task,
            Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        if (ready != sendStarted.Task)
        {
            throw new TimeoutException("SendMessage was not invoked within timeout.");
        }
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
        var response = new AgentTask
        {
            Id = Guid.NewGuid().ToString(),
            Status = new AgentTaskStatus
            {
                State = TaskState.Completed,
            },
            Artifacts = [new Artifact
            {
                ArtifactId = Guid.NewGuid().ToString(),
                Parts = [new TextPart { Text = "agent output" }],
            }],
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
        var response = new AgentTask
        {
            Id = Guid.NewGuid().ToString(),
            Status = new AgentTaskStatus
            {
                State = TaskState.Failed,
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
        var response = new AgentMessage
        {
            Role = MessageRole.Agent,
            MessageId = Guid.NewGuid().ToString(),
            Parts = [new TextPart { Text = "direct reply" }],
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
        var response = new AgentMessage
        {
            Role = MessageRole.Agent,
            MessageId = Guid.NewGuid().ToString(),
            Parts = [new TextPart { Text = "ok" }],
        };

        var result = A2AExecutionDispatcher.MapA2AResponseToMessage(originalMessage, response);

        result.ShouldNotBeNull();
        result!.From.ShouldBe(originalMessage.To);
        result.To.ShouldBe(originalMessage.From);
        result.ThreadId.ShouldBe(originalMessage.ThreadId);
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
/// Records every dispatcher-proxied A2A POST the dispatcher issues
/// (<see cref="IContainerRuntime.SendHttpJsonAsync"/>) and answers each
/// one with a completed-task JSON-RPC body whose artifact text is the
/// configured response. Replaces the old <c>StubA2AResponder</c> which
/// stubbed the (now removed) HttpClient transport.
/// </summary>
internal sealed class SendHttpJsonRecorder(string responseText)
{
    private readonly string _responseText = responseText;
    private readonly List<(string ContainerId, string Url, byte[] Body)> _calls = new();

    public IReadOnlyList<(string ContainerId, string Url, byte[] Body)> Calls => _calls;

    public Task<ContainerHttpResponse> RespondAsync(
        string containerId, string url, byte[] body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Add((containerId, url, body));
        // A2A v0.3 wire shape: result is a flat A2AResponse (AgentTask or
        // AgentMessage) discriminated by `kind` — no `task`/`message` wrapper —
        // and TaskState serializes as kebab-case ("completed", not the v1
        // SDK's "TASK_STATE_COMPLETED"). Mirrors what Python a2a-sdk emits.
        var responseBody = $$"""
            {
              "jsonrpc": "2.0",
              "id": 1,
              "result": {
                "kind": "task",
                "id": "task-1",
                "contextId": "ctx",
                "status": { "state": "completed" },
                "artifacts": [
                  {
                    "artifactId": "a-1",
                    "parts": [ { "kind": "text", "text": "{{System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(_responseText)}}" } ]
                  }
                ]
              }
            }
            """;
        var bytes = System.Text.Encoding.UTF8.GetBytes(responseBody);
        return Task.FromResult(new ContainerHttpResponse(200, bytes));
    }
}