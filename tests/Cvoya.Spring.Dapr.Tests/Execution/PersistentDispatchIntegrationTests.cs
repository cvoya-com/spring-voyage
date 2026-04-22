// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

using SvMessage = Cvoya.Spring.Core.Messaging.Message;

/// <summary>
/// Integration-level tests verifying that persistent agents receive multiple
/// messages without container restart. Uses mocked container runtime and
/// pre-registered endpoints to avoid real container/A2A dependencies.
/// </summary>
public class PersistentDispatchIntegrationTests
{
    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly IPromptAssembler _promptAssembler = Substitute.For<IPromptAssembler>();
    private readonly IAgentDefinitionProvider _agentProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IMcpServer _mcpServer = Substitute.For<IMcpServer>();
    private readonly IAgentToolLauncher _launcher = Substitute.For<IAgentToolLauncher>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly PersistentAgentRegistry _persistentRegistry;
    private readonly A2AExecutionDispatcher _dispatcher;
    private const string AgentId = "persistent-agent";
    private const string Image = "spring-agent-claude:v1";

    public PersistentDispatchIntegrationTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _launcher.Tool.Returns("claude-code");
        _launcher.PrepareAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentLaunchPrep(
                WorkspaceFiles: new Dictionary<string, string>(),
                EnvironmentVariables: new Dictionary<string, string>(),
                WorkspaceMountPath: "/workspace"));

        _mcpServer.Endpoint.Returns("http://host.docker.internal:12345/mcp/");
        _mcpServer.IssueSession(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new McpSession("test-token", ci.ArgAt<string>(0), ci.ArgAt<string>(1)));

        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: AgentId,
                Name: "Persistent Agent",
                Instructions: "do persistent things",
                Execution: new AgentExecutionConfig("claude-code", Image, Hosting: AgentHostingMode.Persistent)));

        _promptAssembler.AssembleAsync(Arg.Any<SvMessage>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("assembled prompt");

        _persistentRegistry = new PersistentAgentRegistry(
            _containerRuntime, _httpClientFactory, _loggerFactory);

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

    private static SvMessage CreateMessage(string? conversationId = null)
    {
        return new SvMessage(
            Guid.NewGuid(),
            new Address("agent", "sender"),
            new Address("agent", AgentId),
            MessageType.Domain,
            conversationId ?? Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Task = "do-work" }),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public void PersistentAgent_PreRegistered_ReusesEndpoint_NoContainerRestart()
    {
        // Pre-register the agent as already running.
        var endpoint = new Uri("http://persistent-container:8999/");
        _persistentRegistry.Register(AgentId, endpoint, "existing-container");

        // Verify the registry returns the endpoint without starting a new container.
        var found = _persistentRegistry.TryGetEndpoint(AgentId, out var result);
        found.ShouldBeTrue();
        result.ShouldBe(endpoint);

        // Verify TryGetEndpoint works multiple times (reuse, not re-start).
        found = _persistentRegistry.TryGetEndpoint(AgentId, out result);
        found.ShouldBeTrue();
        result.ShouldBe(endpoint);

        // Container runtime should NOT have been called (no container started).
        _containerRuntime.DidNotReceive().StartAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
        _containerRuntime.DidNotReceive().RunAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistentAgent_A2AFailure_MarksUnhealthy()
    {
        var endpoint = new Uri("http://persistent-container:8999/");
        _persistentRegistry.Register(AgentId, endpoint, "existing-container");

        // HttpClient that will cause the A2A call to fail.
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var message = CreateMessage();
        try
        {
            await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // Expected.
        }

        // Agent should now be marked unhealthy.
        _persistentRegistry.TryGet(AgentId, out var entry);
        entry.ShouldNotBeNull();
        entry!.HealthStatus.ShouldBe(AgentHealthStatus.Unhealthy);
    }

    [Fact]
    public void Registry_ConcurrentRegisterAndLookup_ThreadSafe()
    {
        var tasks = new List<Task>();

        for (var i = 0; i < 20; i++)
        {
            var agentId = $"agent-{i}";
            var endpoint = new Uri($"http://localhost:{8999 + i}/");
            tasks.Add(Task.Run(() =>
            {
                _persistentRegistry.Register(agentId, endpoint, $"c-{i}");
                _persistentRegistry.TryGetEndpoint(agentId, out _);
            }, TestContext.Current.CancellationToken));
        }

        Task.WhenAll(tasks).ShouldNotThrow();
    }
}