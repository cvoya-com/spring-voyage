// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using A2A.V0_3;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.EntityFrameworkCore;
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
    private readonly IAgentRuntimeLauncher _launcher = Substitute.For<IAgentRuntimeLauncher>();
    private readonly IRuntimeCatalog _runtimeCatalog = Substitute.For<IRuntimeCatalog>();
    private static readonly Cvoya.Spring.Core.Catalog.AgentRuntime ClaudeRuntime = new(
        Id: "claude",
        DisplayName: "Claude",
        DefaultImage: "ghcr.io/test/claude:latest",
        Launcher: "claude-code-cli",
        ThreadBinding: new ThreadBinding(ThreadBindingKind.CliArg, ArgName: "--resume"),
        SystemPromptInjection: new SystemPromptInjection(SystemPromptInjectionKind.File, FilePath: "AGENTS.md"),
        ModelProviders: new[]
        {
            new AgentRuntimeProviderEdge(
                Id: "anthropic",
                AuthMethod: Cvoya.Spring.Core.Catalog.AuthMethod.Oauth,
                CredentialEnvVar: "CLAUDE_CODE_OAUTH_TOKEN"),
        });
    private readonly IAgentContextBuilder _agentContextBuilder = Substitute.For<IAgentContextBuilder>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly IConnectorRuntimeContextResolver _connectorContext = Substitute.For<IConnectorRuntimeContextResolver>();
    private readonly IConnectorPromptContextResolver _connectorPromptContext = Substitute.For<IConnectorPromptContextResolver>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IContainerRuntime _persistentContainerRuntime = Substitute.For<IContainerRuntime>();
    private readonly PersistentAgentRegistry _persistentRegistry;
    private readonly EphemeralAgentRegistry _ephemeralRegistry;
    private readonly A2AExecutionDispatcher _dispatcher;
    private static readonly Guid AgentGuid = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly string AgentId = AgentGuid.ToString("N");
    private static readonly Guid GhostGuid = new("aaaaaaaa-0000-0000-0000-0000000000ff");
    private static readonly Guid SenderGuid = new("aaaaaaaa-0000-0000-0000-000000000010");
    private static readonly Guid TenantGuid = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid AcmeTenantGuid = new("dddddddd-0000-0000-0000-000000000002");
    private const string Image = "spring-agent-claude:v1";
    private const string ContainerId = "spring-ephemeral-abc";

    private static readonly AgentLaunchSpec DefaultSpec = new(
        EnvironmentVariables: new Dictionary<string, string> { ["SPRING_SYSTEM_PROMPT"] = "prepared" });

    public A2AExecutionDispatcherTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var daprEph = Substitute.For<IDaprSidecarManager>();
        var daprOptions = new DaprSidecarOptions();
        var clmEph = new ContainerLifecycleManager(
            _containerRuntime, daprEph, Options.Create(daprOptions), _loggerFactory);
        var bootstrapAuthStore = Substitute.For<IAgentBootstrapAuthStore>();
        var volumeManager = new AgentVolumeManager(_containerRuntime, bootstrapAuthStore, _loggerFactory);
        _ephemeralRegistry = new EphemeralAgentRegistry(
            _containerRuntime, clmEph, volumeManager, _loggerFactory);

        var persistentServices = new ServiceCollection();
        persistentServices.AddSingleton(_persistentContainerRuntime);
        persistentServices.AddSingleton(_httpClientFactory);
        persistentServices.AddSingleton(_loggerFactory);
        persistentServices.AddSingleton(Substitute.For<IDaprSidecarManager>());
        persistentServices.AddSingleton(Options.Create(daprOptions));
        persistentServices.AddSingleton<ContainerLifecycleManager>();
        persistentServices.AddSingleton<AgentVolumeManager>();
        persistentServices.AddSingleton(Substitute.For<IAgentBootstrapAuthStore>());
        persistentServices.AddSingleton(Substitute.For<IAgentDefinitionProvider>());
        persistentServices.AddSingleton(Substitute.For<IMcpServer>());
        // ADR-0052 §3: PersistentAgentLifecycle resolves the container-facing
        // MCP endpoint from McpServerOptions instead of a started McpServer.
        persistentServices.AddSingleton(Options.Create(new Cvoya.Spring.Dapr.Mcp.McpServerOptions { Port = 5050 }));
        persistentServices.AddSingleton(_launcher);
        persistentServices.AddSingleton<IEnumerable<IAgentRuntimeLauncher>>(
            p => [p.GetRequiredService<IAgentRuntimeLauncher>()]);
        persistentServices.AddSingleton<PersistentAgentRegistry>();
        persistentServices.AddSingleton<PersistentAgentLifecycle>();
        // #2468: registry now persists via EF.
        var dbName = $"A2AExecutionDispatcherTests-{Guid.NewGuid()}";
        persistentServices.AddDbContext<Cvoya.Spring.Dapr.Data.SpringDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        _persistentRegistry = persistentServices
            .BuildServiceProvider()
            .GetRequiredService<PersistentAgentRegistry>();

        // ADR-0038: launcher.Kind matches the catalogue runtime entry's
        // launcher strategy id (claude-code-cli for the claude runtime).
        _launcher.Kind.Returns("claude-code-cli");
        _launcher.PrepareAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .Returns(DefaultSpec);

        // ADR-0038: catalogue maps the runtime id ("claude") to the
        // launcher strategy id ("claude-code-cli") so the dispatcher can
        // derive the launcher from the agent definition's runtime slot.
        _runtimeCatalog.GetAgentRuntime(Arg.Any<string>()).Returns((Cvoya.Spring.Core.Catalog.AgentRuntime?)null);
        _runtimeCatalog.GetAgentRuntime("claude").Returns(ClaudeRuntime);

        // D3a: the context builder returns a minimal bootstrap bundle so the
        // dispatcher's MergeBootstrapContext does not crash during tests.
        _agentContextBuilder.BuildAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentBootstrapContext(
                EnvironmentVariables: new Dictionary<string, string>
                {
                    ["SPRING_TENANT_ID"] = TenantGuid.ToString("N"),
                    ["SPRING_AGENT_ID"] = AgentId,
                    ["SPRING_MCP_URL"] = "http://host.docker.internal:12345/mcp/",
                    ["SPRING_MCP_TOKEN"] = "test-token",
                    ["SPRING_LLM_PROVIDER_URL"] = "http://ollama:11434",
                    ["SPRING_LLM_PROVIDER_TOKEN"] = "test-llm-token",
                    ["SPRING_BUCKET2_TOKEN"] = "test-bucket2-token",
                    ["SPRING_WORKSPACE_PATH"] = AgentWorkspaceContract.BuildMountPath(AgentId),
                    ["SPRING_CONCURRENT_THREADS"] = "true",
                }));

        _mcpServer.Endpoint.Returns("http://host.docker.internal:12345/mcp/");
        // The dispatcher calls IssueSession(agentId, threadId, scheme,
        // messageId); the production server materialises a Subject Address
        // from those args (#2379) and carries the inbound message id
        // (ADR-0051). Mirror that here so the returned McpSession satisfies
        // the non-nullable Subject contract.
        _mcpServer.IssueSession(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>())
            .Returns(ci => new McpSession(
                "test-token",
                ci.ArgAt<string>(0),
                ci.ArgAt<string>(1),
                ci.ArgAt<string>(2),
                Address.For(ci.ArgAt<string>(2), ci.ArgAt<string>(0)),
                ci.ArgAt<Guid>(3)));
        _tenantContext.CurrentTenantId.Returns(TenantGuid);

        // #2380: by default no connector contributions (the dispatch path
        // tests do not exercise the connector seam).
        _connectorContext.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(ConnectorRuntimeContextContribution.Empty);

        // #2442: by default no connector prompt fragments (the dispatch
        // path tests do not exercise the prompt-context seam).
        _connectorPromptContext.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());

        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: AgentId,
                Name: "My Agent",
                Instructions: "do things",
                Execution: new AgentExecutionConfig(Runtime: "claude", Image: Image,
                    Hosting: AgentHostingMode.Ephemeral)));

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
        var volumeManagerForDispatcher = new AgentVolumeManager(
            _containerRuntime, Substitute.For<IAgentBootstrapAuthStore>(), _loggerFactory);

        // D2 / Stage 2 of ADR-0029: supply the transport factory that the
        // dispatcher now requires. The factory wraps _containerRuntime so
        // the existing stub wiring (SendHttpJsonAsync → recorder) is
        // preserved end-to-end.
        var transportFactory = new DispatcherProxyA2ATransportFactory(_containerRuntime);

        _dispatcher = new A2AExecutionDispatcher(
            _containerRuntime,
            _promptAssembler,
            _agentProvider,
            _mcpServer,
            [_launcher],
            _runtimeCatalog,
            _agentContextBuilder,
            _tenantContext,
            _persistentRegistry,
            _ephemeralRegistry,
            clmD,
            volumeManagerForDispatcher,
            Options.Create(daprOptions),
            transportFactory,
            _connectorContext,
            _connectorPromptContext,
            new AgentDefinitionSerializer(_runtimeCatalog),
            _loggerFactory);
    }

    private static SvMessage CreateMessage(
        Guid? toGuid = null,
        string? threadId = null,
        JsonElement? payload = null)
    {
        return new SvMessage(
            Guid.NewGuid(),
            new Address("agent", SenderGuid),
            new Address("agent", toGuid ?? AgentGuid),
            MessageType.Domain,
            threadId ?? Guid.NewGuid().ToString(),
            payload ?? JsonSerializer.SerializeToElement(new { Task = "do-work" }),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Wires the container runtime so both halves of the A2A roundtrip — the
    /// readiness probe AND the JSON-RPC <c>message/send</c> POST — answer
    /// successfully. Both legs go through <see cref="IContainerRuntime"/>
    /// since #1160 closed: <see cref="IContainerRuntime.ProbeContainerHttpAsync"/>
    /// covers readiness (from the host process — no in-container wget, issue #1175)
    /// and <see cref="IContainerRuntime.SendHttpJsonAsync"/>
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

    private static string? ReadFirstUserTextPartFromA2ARequest(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array ||
            parts.GetArrayLength() == 0)
        {
            return null;
        }

        var first = parts[0];
        if (first.ValueKind != JsonValueKind.Object ||
            !first.TryGetProperty("text", out var text) ||
            text.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return text.GetString();
    }

    private static string? ReadContextIdFromA2ARequest(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("contextId", out var contextId) ||
            contextId.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return contextId.GetString();
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

        // ADR-0055: ContainerConfig no longer carries Workspace/ContextWorkspace —
        // the agent-sidecar pulls the bundle. Pin only the image and the
        // builder-honoured WorkingDirectory.
        var expected = ContainerConfigBuilder.Build(Image, DefaultSpec);
        await _containerRuntime.Received(1).StartAsync(
            Arg.Is<ContainerConfig>(c =>
                c.Image == expected.Image &&
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
                    Runtime: "claude",
                    Image: Image,
                    Model: new Cvoya.Spring.Core.Catalog.Model("openai", "gpt-4o-mini"))));
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
        var threadId = Guid.Parse(message.ThreadId!);
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("the prompt");
        InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        // The dispatcher threads message.To.Scheme into IssueSession so the
        // server can materialise the session subject (#2379) and the inbound
        // message id so messaging tools carry per-turn authority (ADR-0051).
        _mcpServer.Received(1).IssueSession(
            AgentId, message.ThreadId!, message.To.Scheme, message.Id);
        await _launcher.Received(1).PrepareAsync(
            Arg.Is<AgentLaunchContext>(ctx =>
                ctx.AgentId == AgentId &&
                ctx.ThreadId == message.ThreadId &&
                ctx.McpToken == "test-token" &&
                ctx.McpEndpoint == "http://host.docker.internal:12345/mcp/" &&
                ctx.Prompt == "the prompt" &&
                ctx.AgentAddress == message.To &&
                ctx.CallbackThreadId == threadId &&
                ctx.MessageId == message.Id),
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
    public async Task DispatchAsync_EphemeralAgent_ReadinessProbe_DispatchedThroughHostProbe()
    {
        // #1175: the readiness probe must go through
        // IContainerRuntime.ProbeContainerHttpAsync — no in-container wget,
        // no podman exec. The dispatcher host resolves the container's IP
        // and issues a plain HTTP GET from its own process, so the probe
        // works for any base image (Alpine, distroless, BYOI).
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
    public async Task DispatchAsync_EphemeralAgent_EmitsContextIdInDashedUuidForm()
    {
        // Spring Voyage stores thread ids in no-dash GuidFormatter "N" form, but
        // the A2A wire boundary externalizes them as standard 8-4-4-4-12 dashed
        // UUIDs so CLI agents reached via the bridge (Claude Code's
        // `--session-id`, etc.) accept them as valid session identifiers.
        var threadGuid = Guid.Parse("eeeeeeee-1111-2222-3333-444444444444");
        var message = CreateMessage(threadId: GuidFormatter.Format(threadGuid));
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("the prompt");
        var recorder = InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        recorder.Calls.Count.ShouldBe(1);
        ReadContextIdFromA2ARequest(recorder.Calls[0].Body)
            .ShouldBe(threadGuid.ToString("D"));
    }

    [Fact]
    public async Task DispatchAsync_AttachesPerTurnMcpTokenToTheA2AMessageMetadata()
    {
        // ADR-0052 §4: the per-turn MCP session token is delivered to the
        // agent container in the A2A message/send metadata under the
        // `mcpToken` key. The TypeScript bridge reads this field and
        // rewrites the `spring-voyage` MCP server block's Authorization
        // header in `.mcp.json` before spawning the CLI.
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("the prompt");
        var recorder = InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        recorder.Calls.Count.ShouldBe(1);
        using var document = JsonDocument.Parse(recorder.Calls[0].Body);
        var metadata = document.RootElement
            .GetProperty("params")
            .GetProperty("message")
            .GetProperty("metadata");
        metadata.GetProperty("mcpToken").GetString().ShouldBe("test-token");
    }

    [Fact]
    public async Task DispatchAsync_PersistentAgent_WarmContainer_IssuesPerTurnSessionAndRevokes()
    {
        // ADR-0052 §4: every persistent dispatch — including the warm-
        // container path — issues exactly one per-turn MCP session scoped
        // to the real per-turn thread id + message id (NOT the stable
        // `persistent-{agentId}` container-identity thread), delivers the
        // token in the A2A message/send metadata, and revokes the session
        // at turn end.
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId,
                "My Agent",
                "instructions",
                new AgentExecutionConfig(Runtime: "claude", Image: Image, Hosting: AgentHostingMode.Persistent)));
        _promptAssembler.AssembleAsync(Arg.Any<SvMessage>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");

        await _persistentRegistry.RegisterAsync(
            AgentId,
            new Uri($"http://localhost:{A2AExecutionDispatcher.SidecarPort}/"),
            "existing-container",
            cancellationToken: TestContext.Current.CancellationToken);

        var message = CreateMessage(threadId: Guid.NewGuid().ToString("D"));
        _persistentContainerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        var recorder = InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        // The session is scoped to the real per-turn thread id + message id.
        _mcpServer.Received(1).IssueSession(
            AgentId, message.ThreadId!, message.To.Scheme, message.Id);
        _mcpServer.Received(1).RevokeSession("test-token");

        // The token rides on the A2A message/send metadata.
        recorder.Calls.Count.ShouldBe(1);
        using var document = JsonDocument.Parse(recorder.Calls[0].Body);
        document.RootElement
            .GetProperty("params")
            .GetProperty("message")
            .GetProperty("metadata")
            .GetProperty("mcpToken")
            .GetString()
            .ShouldBe("test-token");
    }

    [Fact]
    public async Task DispatchAsync_PersistentAgent_A2ACallThrows_StillRevokesPerTurnSession()
    {
        // ADR-0052 §4: the per-turn session is revoked in a finally block,
        // so a failed dispatch does not leak the session.
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId,
                "My Agent",
                "instructions",
                new AgentExecutionConfig(Runtime: "claude", Image: Image, Hosting: AgentHostingMode.Persistent)));
        _promptAssembler.AssembleAsync(Arg.Any<SvMessage>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");

        await _persistentRegistry.RegisterAsync(
            AgentId,
            new Uri($"http://localhost:{A2AExecutionDispatcher.SidecarPort}/"),
            "existing-container",
            cancellationToken: TestContext.Current.CancellationToken);

        var message = CreateMessage(threadId: Guid.NewGuid().ToString("D"));
        _persistentContainerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _containerRuntime.SendHttpJsonAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var act = () => _dispatcher.DispatchAsync(
            message, context: null, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<Exception>(act);

        _mcpServer.Received(1).IssueSession(
            AgentId, message.ThreadId!, message.To.Scheme, message.Id);
        _mcpServer.Received(1).RevokeSession("test-token");
    }

    [Fact]
    public async Task DispatchAsync_PersistentAgent_ColdStart_DoesNotIssueLaunchTimeSession()
    {
        // ADR-0052 §3/§4: the cold-start (auto-start) path no longer issues
        // a launch-time MCP session. The launch context carries an empty
        // McpToken — the only session a persistent container ever sees is
        // the per-turn session issued by DispatchPersistentAsync, scoped to
        // the real per-turn thread id, NOT the `persistent-{agentId}`
        // container-identity thread.
        var message = CreateMessage(threadId: Guid.NewGuid().ToString("D"));
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId, "My Agent", "instructions",
                new AgentExecutionConfig(Runtime: "claude", Image: Image, Hosting: AgentHostingMode.Persistent)));
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");

        // StartAsync returns a container id, but the readiness probe fails
        // (no real server) so the dispatch throws after the launch. Use a
        // short cancellation budget to avoid the full 60s readiness wait.
        _containerRuntime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns("spring-persistent-cold");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        var act = () => _dispatcher.DispatchAsync(message, context: null, cts.Token);
        await Should.ThrowAsync<Exception>(act);

        // The launch context the launcher saw carried an empty MCP token —
        // the launch path issues no session of its own.
        await _launcher.Received().PrepareAsync(
            Arg.Is<AgentLaunchContext>(ctx => ctx.McpToken == string.Empty),
            Arg.Any<CancellationToken>());

        // No session was ever issued against the stable container-identity
        // thread `persistent-{agentId}` — only the per-turn thread.
        _mcpServer.DidNotReceive().IssueSession(
            Arg.Any<string>(), $"persistent-{AgentId}", Arg.Any<string>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task DispatchAsync_PersistentAgent_SuccessfulSend_BumpsRegistryUpdatedAt()
    {
        // #2519 part 3: a successful A2A POST is the strongest possible
        // "the container is alive" signal. The dispatcher records a
        // freshness heartbeat by bumping the runtime row's UpdatedAt so a
        // sibling host's health-sweep freshness gate (#2519 part 1) skips
        // an otherwise-scheduled restart against this still-busy agent.
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId,
                "My Agent",
                "instructions",
                new AgentExecutionConfig(Runtime: "claude", Image: Image, Hosting: AgentHostingMode.Persistent)));
        _promptAssembler.AssembleAsync(Arg.Any<SvMessage>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");

        await _persistentRegistry.RegisterAsync(
            AgentId,
            new Uri($"http://localhost:{A2AExecutionDispatcher.SidecarPort}/"),
            "existing-container",
            cancellationToken: TestContext.Current.CancellationToken);

        var before = await _persistentRegistry.TryGetAsync(AgentId, TestContext.Current.CancellationToken);
        before.ShouldNotBeNull();
        var beforeUpdatedAt = before!.UpdatedAt;

        // Give the wall clock at least one tick of separation so the
        // strictly-greater assertion is not sensitive to clock resolution.
        await Task.Delay(10, TestContext.Current.CancellationToken);

        var message = CreateMessage(threadId: Guid.NewGuid().ToString("D"));
        // Pre-flight probe runs on the registry's container runtime, not the
        // dispatcher's — configure both so the dispatch completes.
        _persistentContainerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        InstallA2AStub();

        var result = await _dispatcher.DispatchAsync(
            message, context: null, TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();

        var after = await _persistentRegistry.TryGetAsync(AgentId, TestContext.Current.CancellationToken);
        after.ShouldNotBeNull();
        after!.UpdatedAt.ShouldBeGreaterThan(beforeUpdatedAt);
    }

    [Fact]
    public async Task DispatchAsync_PersistentAgent_A2ACallThrows_DoesNotBumpUpdatedAt()
    {
        // The heartbeat is gated on a successful send only — a thrown
        // A2A call marks the row Unhealthy via MarkUnhealthyAsync (which
        // intentionally does NOT bump UpdatedAt, #2519). Without this
        // asymmetry, a failed dispatch would still advance the freshness
        // gate and hide an actual outage from sibling restart paths.
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId,
                "My Agent",
                "instructions",
                new AgentExecutionConfig(Runtime: "claude", Image: Image, Hosting: AgentHostingMode.Persistent)));
        _promptAssembler.AssembleAsync(Arg.Any<SvMessage>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");

        await _persistentRegistry.RegisterAsync(
            AgentId,
            new Uri($"http://localhost:{A2AExecutionDispatcher.SidecarPort}/"),
            "existing-container",
            cancellationToken: TestContext.Current.CancellationToken);

        var before = await _persistentRegistry.TryGetAsync(AgentId, TestContext.Current.CancellationToken);
        before.ShouldNotBeNull();
        var beforeUpdatedAt = before!.UpdatedAt;

        await Task.Delay(10, TestContext.Current.CancellationToken);

        var message = CreateMessage(threadId: Guid.NewGuid().ToString("D"));
        // Pre-flight probe goes through PersistentAgentRegistry.ProbeLivenessAsync,
        // which uses the registry's container runtime (not the dispatcher's).
        // Configure both so the pre-flight passes; SendHttpJsonAsync on the
        // dispatcher's runtime throws so the catch path marks Unhealthy.
        _persistentContainerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _containerRuntime.SendHttpJsonAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var act = () => _dispatcher.DispatchAsync(
            message, context: null, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<Exception>(act);

        var after = await _persistentRegistry.TryGetAsync(AgentId, TestContext.Current.CancellationToken);
        after.ShouldNotBeNull();
        // Status flipped to Unhealthy by MarkUnhealthyAsync, but UpdatedAt
        // is unchanged: the freshness gate must distinguish "container
        // alive" signals from "container died" flags.
        after!.HealthStatus.ShouldBe(AgentHealthStatus.Unhealthy);
        after.UpdatedAt.ShouldBe(beforeUpdatedAt);
    }

    [Fact]
    public async Task DispatchAsync_PersistentAgent_MalformedThreadId_ThrowsBeforeAnyDispatchWork()
    {
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId,
                "My Agent",
                "instructions",
                new AgentExecutionConfig(Runtime: "claude", Image: Image, Hosting: AgentHostingMode.Persistent)));
        _promptAssembler.AssembleAsync(Arg.Any<SvMessage>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");

        await _persistentRegistry.RegisterAsync(
            AgentId,
            new Uri($"http://localhost:{A2AExecutionDispatcher.SidecarPort}/"),
            "existing-container",
            cancellationToken: TestContext.Current.CancellationToken);

        var message = CreateMessage(threadId: "not-a-guid");

        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<SpringException>(act);

        ex.Message.ShouldContain("malformed thread id");
        await _containerRuntime.DidNotReceive().SendHttpJsonAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<byte[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_UnknownAgent_Throws()
    {
        var message = CreateMessage(toGuid: GhostGuid);
        _agentProvider.GetByIdAsync(GhostGuid.ToString("N"), Arg.Any<CancellationToken>())
            .Returns((AgentDefinition?)null);

        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<SpringException>(act);
        ex.Message.ShouldContain("No agent definition");
    }

    [Fact]
    public async Task DispatchAsync_DefinitionWithoutExecution_ThrowsWithGuidance()
    {
        // #2208 / #2224: backstops the visibility chain. The provider now
        // returns a definition with Execution: null when a unit row exists
        // but lacks `execution.agent`; the dispatcher must turn that into a
        // SpringException citing the missing execution configuration so the
        // coordinator can emit a visible ErrorOccurred activity row.
        // Without this guarantee, UnitRuntimeDispatchVisibilityTests (which
        // mocks the dispatcher) is a green-but-meaningless assertion.
        var message = CreateMessage();
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: AgentId,
                Name: "Misconfigured",
                Instructions: "instructions",
                Execution: null));

        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<SpringException>(act);

        ex.Message.ShouldContain(AgentId);
        ex.Message.ShouldContain("no execution configuration");

        // No container side-effects — the throw must precede any dispatch work.
        await _containerRuntime.DidNotReceive().StartAsync(
            Arg.Any<ContainerConfig>(),
            Arg.Any<CancellationToken>());
        await _containerRuntime.DidNotReceive().SendHttpJsonAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<byte[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_AgentClaude_SelectsClaudeCodeCliLauncher()
    {
        // #1732 regression: setting ai.agent: claude on a unit / agent
        // resolves end-to-end through the runtime registry to the launcher
        // whose Kind is claude-code-cli. The agent definition does not
        // carry a Tool field; the dispatcher derives it.
        var message = CreateMessage();
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: AgentId,
                Name: "My Agent",
                Instructions: null,
                Execution: new AgentExecutionConfig(Runtime: "claude", Image: Image)));
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");
        InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        // The Claude runtime resolved through the catalogue, and the
        // launcher whose Kind matches (claude-code-cli) was used to
        // prepare the launch context.
        _runtimeCatalog.Received().GetAgentRuntime("claude");
        await _launcher.Received().PrepareAsync(
            Arg.Any<AgentLaunchContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_UnknownAgentRuntime_Throws()
    {
        // #1732: an unregistered runtime id is rejected with a clear error
        // before launcher lookup happens.
        var message = CreateMessage();
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId, "My Agent", null,
                new AgentExecutionConfig(Runtime: "not-a-runtime", Image: Image)));

        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<SpringException>(act);
        ex.Message.ShouldContain("No agent runtime is registered");
    }

    [Fact]
    public async Task DispatchAsync_PersistentAgent_NotRunning_AttemptsAutoStart()
    {
        var message = CreateMessage();
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId, "My Agent", "instructions",
                new AgentExecutionConfig(Runtime: "claude", Image: Image, Hosting: AgentHostingMode.Persistent)));
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
                new AgentExecutionConfig(Runtime: "claude", Image: Image, Hosting: AgentHostingMode.Persistent)));
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");
        _containerRuntime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns("spring-persistent-cc");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        try { await _dispatcher.DispatchAsync(message, context: null, cts.Token); }
        catch { /* readiness probe will fail; assertion on the StartAsync call is what we want */ }

        // ADR-0055: ContainerConfig no longer carries Workspace; pin only the image.
        var expected = ContainerConfigBuilder.Build(Image, DefaultSpec);
        await _containerRuntime.Received(1).StartAsync(
            Arg.Is<ContainerConfig>(c => c.Image == expected.Image),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_NullImage_Throws()
    {
        var message = CreateMessage();
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId, "My Agent", null,
                new AgentExecutionConfig(Runtime: "claude", Image: null)));
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
                new AgentExecutionConfig(Runtime: "claude", Image: Image, Hosting: AgentHostingMode.Pooled)));
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
                EnvironmentVariables: new Dictionary<string, string>
                {
                    ["SPRING_SYSTEM_PROMPT"] = ci.ArgAt<AgentLaunchContext>(0).Prompt
                }));

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
        // (see #1160 / #1175). Readiness answers healthy immediately so the
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
    public async Task DispatchAsync_DefaultHostingMode_IsPersistent()
    {
        // Ensure that AgentExecutionConfig with no explicit hosting defaults to Persistent (#2085)
        var config = new AgentExecutionConfig(Runtime: "claude", Image: Image);
        config.Hosting.ShouldBe(AgentHostingMode.Persistent);

        // Override the default agent provider to return an agent with no explicit hosting
        // so the dispatch path exercises the new Persistent default.
        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: AgentId,
                Name: "My Agent",
                Instructions: "do things",
                Execution: new AgentExecutionConfig(Runtime: "claude", Image: Image)));

        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");
        InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        await _containerRuntime.Received(1).StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_PopulatesAgentDefinitionYamlAndTenantIdOnLaunchContext()
    {
        // #1321: dispatcher must populate AgentDefinitionYaml and TenantId on
        // the AgentLaunchContext so AgentContextBuilder can write the
        // /spring/context/ files.
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");
        _tenantContext.CurrentTenantId.Returns(AcmeTenantGuid);
        InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        await _launcher.Received(1).PrepareAsync(
            Arg.Is<AgentLaunchContext>(ctx =>
                ctx.TenantId == AcmeTenantGuid &&
                ctx.AgentDefinitionYaml != null &&
                ctx.AgentDefinitionYaml.Contains("agent_id") &&
                ctx.TenantConfigJson != null &&
                ctx.TenantConfigJson.Contains(AcmeTenantGuid.ToString("N"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_EphemeralAgent_AgentDefinitionYaml_ContainsExpectedFields()
    {
        // #1321: the serialised YAML must include the core agent definition
        // fields so the in-container SDK can read them from agent-definition.yaml.
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("p");
        InstallA2AStub();

        AgentLaunchContext? capturedCtx = null;
        _launcher.PrepareAsync(Arg.Do<AgentLaunchContext>(ctx => capturedCtx = ctx), Arg.Any<CancellationToken>())
            .Returns(new AgentLaunchSpec(
                EnvironmentVariables: new Dictionary<string, string>()));

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        capturedCtx.ShouldNotBeNull();
        capturedCtx!.AgentDefinitionYaml.ShouldNotBeNullOrEmpty();
        capturedCtx.AgentDefinitionYaml.ShouldContain(AgentId);  // agent_id field
        // ADR-0038 amendment (#2634): execution.runtime (the runtime
        // registry id) — the YAML includes the derived kind so in-container
        // probes that read it can see which engine was selected.
        capturedCtx.AgentDefinitionYaml.ShouldContain("runtime: claude");
        capturedCtx.AgentDefinitionYaml.ShouldContain("kind: claude-code-cli");
        capturedCtx.TenantConfigJson.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task WaitForA2AReadyAsync_ReadinessTimeoutExpires_ThrowsSpringExceptionAndTearsDownContainer()
    {
        // Arrange: readiness probe always returns not-ready. The dispatcher's
        // internal timeout is shortened to 10 ms so the test completes in
        // well under a second without relying on real wall-clock sleep.
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");

        _containerRuntime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(ContainerId);

        // ProbeContainerHttpAsync never returns healthy — the loop runs until
        // the internal CancelAfter fires the timeout token.
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        // Override the effective timeout so the internal CTS fires at 10 ms
        // rather than 60 s. The outer CancellationToken is not cancelled, so
        // the exception that surfaces must come from the timeout branch (not
        // the caller's cancel), which the dispatcher maps to SpringException.
        _dispatcher.EffectiveReadinessTimeout = TimeSpan.FromMilliseconds(10);

        // Act
        var act = () => _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<SpringException>(act);

        // Assert: correct exception text from the timeout branch
        ex.Message.ShouldContain("did not become A2A-ready");

        // Assert: container teardown fires exactly once via the registry's
        // release path (StopAsync), even though no outer token was cancelled.
        await _containerRuntime.Received(1).StopAsync(ContainerId, Arg.Any<CancellationToken>());
        _ephemeralRegistry.GetAllEntries().ShouldBeEmpty();
    }

    [Fact]
    public async Task PollTaskUntilTerminalAsync_TaskPollingTimeoutExpires_ReturnsNonTerminalPayloadAndTearsDownContainer()
    {
        // Arrange: readiness probe returns healthy immediately; every
        // message/send and tasks/get call returns a non-terminal (Submitted)
        // task. The task-terminal timeout is shortened to 10 ms so the
        // polling loop's internal CancelAfter fires before the first
        // TaskPollInterval delay (500 ms) elapses.
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("prompt");

        _containerRuntime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(ContainerId);

        // Readiness probe passes immediately so dispatch proceeds to the A2A roundtrip.
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Every SendHttpJsonAsync call (both message/send and tasks/get)
        // returns a Submitted (non-terminal) task so the polling loop never
        // exits on its own — only the internal timeout breaks it.
        const string submittedTaskJson =
            """
            {
              "jsonrpc": "2.0",
              "id": 1,
              "result": {
                "kind": "task",
                "id": "task-polling-timeout",
                "contextId": "ctx",
                "status": { "state": "submitted" }
              }
            }
            """;
        _containerRuntime.SendHttpJsonAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ContainerHttpResponse(
                200,
                System.Text.Encoding.UTF8.GetBytes(submittedTaskJson))));

        // Override the task-terminal timeout to 10 ms. The readiness timeout
        // can stay default — the probe succeeds on the first attempt anyway.
        _dispatcher.EffectiveTaskTerminalTimeout = TimeSpan.FromMilliseconds(10);

        // Act: dispatch completes (no exception — timeout in the polling loop
        // is not fatal; the dispatcher returns the last-known non-terminal task
        // mapped through MapA2AResponseToMessage).
        var result = await _dispatcher.DispatchAsync(
            message, context: null, TestContext.Current.CancellationToken);

        // Assert: the non-terminal task maps to ExitCode = 1.
        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<System.Text.Json.JsonElement>();
        payload.GetProperty("ExitCode").GetInt32().ShouldBe(1);

        // Assert: container teardown fires exactly once via the registry —
        // the finally block in DispatchEphemeralAsync always releases the
        // lease regardless of polling outcome.
        await _containerRuntime.Received(1).StopAsync(ContainerId, Arg.Any<CancellationToken>());
        _ephemeralRegistry.GetAllEntries().ShouldBeEmpty();
    }

    /// <summary>
    /// Regression cover for #2230. Bare-string payloads (the CLI / API
    /// send shape) used to fall through the dispatcher's narrower
    /// Task-only extraction and leave the user-role text part defaulted
    /// to the assembled system prompt. The dispatcher must now route
    /// every payload shape through the shared payload-text helper so
    /// the user role carries the actual user message.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_BareStringPayload_UserTextPartCarriesPayloadNotPrompt()
    {
        var message = CreateMessage(
            payload: JsonSerializer.SerializeToElement("can you list the agents that you have in your unit?"));
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("## Platform Instructions\nyou are an agent...");
        var recorder = InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        recorder.Calls.Count.ShouldBe(1);
        var userText = ReadFirstUserTextPartFromA2ARequest(recorder.Calls[0].Body);
        userText.ShouldBe("can you list the agents that you have in your unit?");
        userText!.ShouldNotContain("Platform Instructions");
    }

    /// <summary>
    /// Regression cover for #2230, agent-turn wrapper variant. A
    /// <c>{ text: "…" }</c> payload (the agent-turn wrap shape) must
    /// also reach the user role unchanged.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_TextWrappedPayload_UserTextPartCarriesPayloadNotPrompt()
    {
        var message = CreateMessage(
            payload: JsonSerializer.SerializeToElement(new { text = "wrapped via text" }));
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("system prompt body");
        var recorder = InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        recorder.Calls.Count.ShouldBe(1);
        ReadFirstUserTextPartFromA2ARequest(recorder.Calls[0].Body).ShouldBe("wrapped via text");
    }

    /// <summary>
    /// The pre-fix Task-shape payload still maps to the user role —
    /// the rewritten extraction is a strict superset of the previous
    /// behaviour for object-shaped payloads.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_TaskWrappedPayload_UserTextPartCarriesPayload()
    {
        var message = CreateMessage(
            payload: JsonSerializer.SerializeToElement(new { Task = "wrapped via Task" }));
        _promptAssembler.AssembleAsync(message, Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("system prompt body");
        var recorder = InstallA2AStub();

        await _dispatcher.DispatchAsync(message, context: null, TestContext.Current.CancellationToken);

        recorder.Calls.Count.ShouldBe(1);
        ReadFirstUserTextPartFromA2ARequest(recorder.Calls[0].Body).ShouldBe("wrapped via Task");
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
