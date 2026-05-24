// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

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
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly IAgentRuntimeLauncher _launcher = Substitute.For<IAgentRuntimeLauncher>();
    private readonly IRuntimeCatalog _runtimeCatalog = Substitute.For<IRuntimeCatalog>();
    private readonly IAgentContextBuilder _agentContextBuilder = Substitute.For<IAgentContextBuilder>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly IConnectorRuntimeContextResolver _connectorContext = Substitute.For<IConnectorRuntimeContextResolver>();
    private readonly IConnectorPromptContextResolver _connectorPromptContext = Substitute.For<IConnectorPromptContextResolver>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly PersistentAgentRegistry _persistentRegistry;
    private readonly A2AExecutionDispatcher _dispatcher;
    private static readonly Guid AgentGuid = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly string AgentId = AgentGuid.ToString("N");
    private static readonly Guid SenderGuid = new("aaaaaaaa-1111-1111-1111-000000000002");
    private const string Image = "spring-agent-claude:v1";

    public PersistentDispatchIntegrationTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var daprOptions = new DaprSidecarOptions();
        _launcher.Kind.Returns("claude-code-cli");
        // ADR-0038: catalogue maps the runtime id ("claude") to the
        // launcher strategy id so the dispatcher can derive the launcher.
        var claudeRuntime = new Cvoya.Spring.Core.Catalog.AgentRuntime(
            Id: "claude",
            DisplayName: "Claude",
            DefaultImage: "ghcr.io/test/claude:latest",
            Launcher: "claude-code-cli",
            ThreadBinding: new ThreadBinding(ThreadBindingKind.CliArg, ArgName: "--resume"),
            ModelProviders: new[]
            {
                new AgentRuntimeProviderEdge(
                    Id: "anthropic",
                    AuthMethod: AuthMethod.Oauth,
                    CredentialEnvVar: "CLAUDE_CODE_OAUTH_TOKEN"),
            });
        _runtimeCatalog.GetAgentRuntime("claude").Returns(claudeRuntime);
        _launcher.PrepareAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentLaunchSpec(
                EnvironmentVariables: new Dictionary<string, string>()));

        // D3a: return a minimal bootstrap bundle.
        _agentContextBuilder.BuildAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentBootstrapContext(
                EnvironmentVariables: new Dictionary<string, string>
                {
                    ["SPRING_TENANT_ID"] = Cvoya.Spring.Core.Tenancy.OssTenantIds.DefaultNoDash,
                    ["SPRING_AGENT_ID"] = AgentId,
                }));

        _mcpServer.Endpoint.Returns("http://host.docker.internal:12345/mcp/");
        // Production dispatch threads message.To.Scheme and the inbound
        // message id into IssueSession so the McpSession carries a
        // materialised Subject Address (#2379) and per-turn delivery
        // authority (ADR-0051). The mock mirrors that.
        _mcpServer.IssueSession(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>())
            .Returns(ci => new McpSession(
                "test-token",
                ci.ArgAt<string>(0),
                ci.ArgAt<string>(1),
                ci.ArgAt<string>(2),
                Address.For(ci.ArgAt<string>(2), ci.ArgAt<string>(0)),
                ci.ArgAt<Guid>(3)));
        _tenantContext.CurrentTenantId.Returns(Cvoya.Spring.Core.Tenancy.OssTenantIds.Default);

        // #2380: default to "no connector contribution" — these tests do not
        // exercise the connector seam.
        _connectorContext.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(ConnectorRuntimeContextContribution.Empty);

        // #2442: default to "no connector prompt fragments".
        _connectorPromptContext.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());

        _agentProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: AgentId,
                Name: "Persistent Agent",
                Instructions: "do persistent things",
                Execution: new AgentExecutionConfig(Runtime: "claude", Image: Image, Hosting: AgentHostingMode.Persistent)));

        _promptAssembler.AssembleAsync(Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns("assembled prompt");

        var persistentServices = new ServiceCollection();
        persistentServices.AddSingleton(_containerRuntime);
        persistentServices.AddSingleton(_httpClientFactory);
        persistentServices.AddSingleton(_loggerFactory);
        persistentServices.AddSingleton(Substitute.For<IDaprSidecarManager>());
        persistentServices.AddSingleton(Options.Create(daprOptions));
        persistentServices.AddSingleton<ContainerLifecycleManager>();
        persistentServices.AddSingleton<AgentVolumeManager>();
        persistentServices.AddSingleton(Substitute.For<IAgentBootstrapAuthStore>());
        persistentServices.AddSingleton(Substitute.For<IAgentDefinitionProvider>());
        persistentServices.AddSingleton(_mcpServer);
        // ADR-0052 §3: PersistentAgentLifecycle resolves the container-facing
        // MCP endpoint from McpServerOptions instead of a started McpServer.
        persistentServices.AddSingleton(Options.Create(new Cvoya.Spring.Dapr.Mcp.McpServerOptions { Port = 5050 }));
        persistentServices.AddSingleton(_launcher);
        persistentServices.AddSingleton<IEnumerable<IAgentRuntimeLauncher>>(
            p => [p.GetRequiredService<IAgentRuntimeLauncher>()]);
        persistentServices.AddSingleton(_runtimeCatalog);
        persistentServices.AddSingleton<PersistentAgentRegistry>();
        persistentServices.AddSingleton<PersistentAgentLifecycle>();
        // #2468: registry now persists via EF.
        var dbName = $"PersistentDispatchIntegrationTests-{Guid.NewGuid()}";
        persistentServices.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        _persistentRegistry = persistentServices
            .BuildServiceProvider()
            .GetRequiredService<PersistentAgentRegistry>();

        var daprEph = Substitute.For<IDaprSidecarManager>();
        var clmEph = new ContainerLifecycleManager(
            _containerRuntime, daprEph, Options.Create(daprOptions), _loggerFactory);
        var daprD = Substitute.For<IDaprSidecarManager>();
        var clmD = new ContainerLifecycleManager(
            _containerRuntime, daprD, Options.Create(daprOptions), _loggerFactory);
        var bootstrapAuthStoreForDispatcher = Substitute.For<IAgentBootstrapAuthStore>();
        var volumeManager = new AgentVolumeManager(_containerRuntime, bootstrapAuthStoreForDispatcher, _loggerFactory);

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
            new EphemeralAgentRegistry(_containerRuntime, clmEph, volumeManager, _loggerFactory),
            clmD,
            volumeManager,
            Options.Create(daprOptions),
            transportFactory,
            _connectorContext,
            _connectorPromptContext,
            _loggerFactory);
    }

    private static SvMessage CreateMessage(string? threadId = null)
    {
        return new SvMessage(
            Guid.NewGuid(),
            new Address("agent", SenderGuid),
            new Address("agent", AgentGuid),
            MessageType.Domain,
            threadId ?? Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Task = "do-work" }),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task PersistentAgent_PreRegistered_ReusesEndpoint_NoContainerRestart()
    {
        // Pre-register the agent as already running.
        var endpoint = new Uri("http://persistent-container:8999/");
        await _persistentRegistry.RegisterAsync(AgentId, endpoint, "existing-container", cancellationToken: TestContext.Current.CancellationToken);

        // Verify the registry returns the endpoint without starting a new container.
        var result = await _persistentRegistry.TryGetEndpointAsync(AgentId, cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldBe(endpoint);

        // Verify TryGetEndpointAsync works multiple times (reuse, not re-start).
        result = await _persistentRegistry.TryGetEndpointAsync(AgentId, cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldBe(endpoint);

        // Container runtime should NOT have been called (no container started).
        await _containerRuntime.DidNotReceive().StartAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
        await _containerRuntime.DidNotReceive().RunAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistentAgent_A2AFailure_MarksUnhealthy()
    {
        var endpoint = new Uri("http://persistent-container:8999/");
        await _persistentRegistry.RegisterAsync(AgentId, endpoint, "existing-container", cancellationToken: TestContext.Current.CancellationToken);

        // #2092 pre-flight probe must succeed so we exercise the
        // mid-dispatch failure path (the SendA2AMessageAsync catch block)
        // rather than the new pre-flight-routed restart path. The crash-
        // detection pre-flight semantics are covered by the dedicated
        // test below (PreFlight_DeadAgent_RoutesToRestart).
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

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
        var entry = await _persistentRegistry.TryGetAsync(AgentId, cancellationToken: TestContext.Current.CancellationToken);
        entry.ShouldNotBeNull();
        entry!.HealthStatus.ShouldBe(AgentHealthStatus.Unhealthy);
    }

    [Fact]
    public async Task PreFlight_DeadAgent_RoutesToRestart_NoLongA2AWait()
    {
        // #2092 acceptance bullet 2: when the registry shows an entry that
        // is no longer reachable, the dispatcher's pre-flight probe catches
        // it before the A2A call goes out, tears the dead container down,
        // and routes through the auto-restart path. The doomed A2A call
        // is never issued.
        var endpoint = new Uri("http://dead-container:8999/");
        await _persistentRegistry.RegisterAsync(AgentId, endpoint, "dead-container", cancellationToken: TestContext.Current.CancellationToken);

        // Probe always returns false — agent endpoint is unreachable.
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // The restart path will land in StartPersistentAgentAsync and try
        // to call StartAsync + WaitForA2AReadyAsync. Both fail under the
        // mock (probe stays false), so the dispatcher will throw with the
        // readiness-timeout error rather than a 30s A2A wire timeout.
        _containerRuntime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns("new-container");

        // Bound the readiness wait so the test doesn't pay the default 60s.
        _dispatcher.EffectiveReadinessTimeout = TimeSpan.FromMilliseconds(50);

        var message = CreateMessage();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SpringException? thrownEx = null;
        try
        {
            await _dispatcher.DispatchAsync(
                message, context: null, TestContext.Current.CancellationToken);
        }
        catch (SpringException ex)
        {
            // Expected — restart fails because mocked probe never goes ready.
            thrownEx = ex;
        }
        sw.Stop();

        // (a) The dispatcher took the readiness-timeout path on relaunch
        // (not the A2A wire-timeout path on the dead container). The
        // exception's message names the readiness branch.
        thrownEx.ShouldNotBeNull();
        thrownEx!.Message.ShouldContain("did not become ready");

        // (b) Old container was torn down before the relaunch attempt.
        // StopContainerAsync (#2092) preserves the volume and stops the
        // dead container, surfacing as IContainerRuntime.StopAsync.
        await _containerRuntime.Received().StopAsync("dead-container", Arg.Any<CancellationToken>());

        // (c) The new container's StartAsync was attempted (the restart
        // path actually ran rather than the A2A path).
        await _containerRuntime.Received().StartAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());

        // (d) The whole pre-flight-then-restart path completes well below
        // the 30s wire-timeout budget that the bug had us paying. Generous
        // ceiling of 5s leaves plenty of headroom for slow CI without
        // missing a regression that re-introduces the long wait.
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PreFlight_HealthyAgent_DoesNotRestart()
    {
        // The pre-flight probe must be transparent on the healthy path:
        // when the agent endpoint answers, the dispatcher reuses the
        // existing entry and goes straight to the A2A call without
        // tearing the container down or re-launching.
        var endpoint = new Uri("http://healthy-container:8999/");
        await _persistentRegistry.RegisterAsync(AgentId, endpoint, "healthy-container", cancellationToken: TestContext.Current.CancellationToken);

        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // The A2A SendHttpJsonAsync will be called via the proxy transport.
        // Return a minimal success body so MapA2AResponseToMessage can
        // shape it without throwing.
        var emptyTaskJson = """
            {
              "jsonrpc": "2.0",
              "id": "1",
              "result": {
                "id": "task-1",
                "contextId": "ctx-1",
                "status": { "state": "completed" },
                "kind": "task"
              }
            }
            """u8.ToArray();
        _containerRuntime.SendHttpJsonAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerHttpResponse(200, emptyTaskJson));

        var message = CreateMessage();
        var response = await _dispatcher.DispatchAsync(
            message, context: null, TestContext.Current.CancellationToken);

        // Container was NOT torn down or restarted.
        await _containerRuntime.DidNotReceive().StopAsync(
            Arg.Is<string>(id => id == "healthy-container"), Arg.Any<CancellationToken>());
        await _containerRuntime.DidNotReceive().StartAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());

        // Entry remains healthy.
        var entry = await _persistentRegistry.TryGetAsync(AgentId, cancellationToken: TestContext.Current.CancellationToken);
        entry.ShouldNotBeNull();
        entry!.HealthStatus.ShouldBe(AgentHealthStatus.Healthy);

        // The A2A call did happen.
        await _containerRuntime.Received().SendHttpJsonAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());

        response.ShouldNotBeNull();
    }

    [Fact]
    public async Task Registry_ConcurrentRegisterAndLookup_ThreadSafe()
    {
        var tasks = new List<Task>();

        for (var i = 0; i < 20; i++)
        {
            var agentGuid = new Guid($"aaaaaaaa-cccc-2222-1111-0000000000{i:D2}");
            var agentId = GuidFormatter.Format(agentGuid);
            var endpoint = new Uri($"http://localhost:{8999 + i}/");
            tasks.Add(Task.Run(async () =>
            {
                await _persistentRegistry.RegisterAsync(agentId, endpoint, $"c-{i}", cancellationToken: TestContext.Current.CancellationToken);
                await _persistentRegistry.TryGetEndpointAsync(agentId, cancellationToken: TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(tasks);
    }
}
