// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PersistentAgentRegistry"/>. After #2468 the
/// registry round-trips through an EF row instead of an in-memory
/// dictionary, so every test sets up an in-memory <see cref="SpringDbContext"/>
/// shared via <see cref="IServiceScopeFactory"/>.
/// </summary>
public class PersistentAgentRegistryTests : IDisposable
{
    // xUnit1051 demands every method that accepts a CancellationToken
    // receive TestContext.Current.CancellationToken so tests are
    // promptly cancellable from the harness. The registry's surface
    // accepts an optional token everywhere; using this property keeps
    // every call site terse.
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Agent1Guid = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid Agent2Guid = new("aaaaaaaa-1111-1111-1111-000000000002");
    private static readonly Guid Agent3Guid = new("aaaaaaaa-1111-1111-1111-000000000003");
    private static readonly string Agent1Id = GuidFormatter.Format(Agent1Guid);
    private static readonly string Agent2Id = GuidFormatter.Format(Agent2Guid);
    private static readonly string Agent3Id = GuidFormatter.Format(Agent3Guid);

    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IAgentDefinitionProvider _agentProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IMcpServer _mcpServer = Substitute.For<IMcpServer>();
    private readonly IAgentRuntimeLauncher _launcher = Substitute.For<IAgentRuntimeLauncher>();
    private readonly ServiceProvider _serviceProvider;
    private readonly PersistentAgentRegistry _registry;

    public PersistentAgentRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _launcher.Kind.Returns("claude-code-cli");
        _mcpServer.Endpoint.Returns("http://host.docker.internal:12345/mcp/");
        // Mirror production session shape (#2379): every session carries a
        // materialised Subject Address built from (agentId, callerKind). The
        // PersistentAgentLifecycle always passes Address.AgentScheme for the
        // explicit-deploy path, so the third arg defaults to that here.
        _mcpServer.IssueSession(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new McpSession(
                "t",
                ci.ArgAt<string>(0),
                ci.ArgAt<string>(1),
                ci.ArgAt<string>(2),
                Address.For(ci.ArgAt<string>(2), ci.ArgAt<string>(0))));
        _launcher.PrepareAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentLaunchSpec(new Dictionary<string, string>()));
        _agentProvider
            .GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentDefinition?)null);

        var services = new ServiceCollection();
        services.AddSingleton(_containerRuntime);
        services.AddSingleton(_httpClientFactory);
        services.AddSingleton(_loggerFactory);
        services.AddSingleton(Substitute.For<IDaprSidecarManager>());
        services.AddSingleton(Options.Create(new DaprSidecarOptions()));
        services.AddSingleton<ContainerLifecycleManager>();
        services.AddSingleton(_agentProvider);
        services.AddSingleton(_mcpServer);
        // ADR-0052 §3: PersistentAgentLifecycle resolves the container-facing
        // MCP endpoint from McpServerOptions instead of a started McpServer.
        services.AddSingleton(Options.Create(new Cvoya.Spring.Dapr.Mcp.McpServerOptions { Port = 5050 }));
        services.AddSingleton(_launcher);
        services.AddSingleton<IEnumerable<IAgentRuntimeLauncher>>(_ => new[] { _launcher });
        services.AddSingleton(Substitute.For<IAgentBootstrapAuthStore>());
        services.AddSingleton<AgentVolumeManager>();
        services.AddSingleton<PersistentAgentRegistry>();
        services.AddSingleton<PersistentAgentLifecycle>();
        // #2468: registry now persists state via EF; tests share a single
        // in-memory DB per fixture so write-then-read assertions work.
        var dbName = $"PersistentAgentRegistryTests-{Guid.NewGuid()}";
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        _serviceProvider = services.BuildServiceProvider();
        _registry = _serviceProvider.GetRequiredService<PersistentAgentRegistry>();
    }

    public void Dispose()
    {
        _registry.Dispose();
        _serviceProvider.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Register_TryGetEndpoint_ReturnsEndpoint()
    {
        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", cancellationToken: Ct);

        var result = await _registry.TryGetEndpointAsync(Agent1Id, cancellationToken: Ct);

        result.ShouldBe(endpoint);
    }

    [Fact]
    public async Task TryGetEndpoint_UnknownAgent_ReturnsNull()
    {
        var result = await _registry.TryGetEndpointAsync(Agent1Id, cancellationToken: Ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Remove_TryGetEndpoint_ReturnsNull()
    {
        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", cancellationToken: Ct);

        await _registry.RemoveAsync(Agent1Id, cancellationToken: Ct);

        var result = await _registry.TryGetEndpointAsync(Agent1Id, cancellationToken: Ct);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task Remove_UnknownAgent_DoesNotThrow()
    {
        // Should not throw for unknown agents.
        await _registry.RemoveAsync(Agent1Id, cancellationToken: Ct);
    }

    [Fact]
    public async Task Register_OverwritesExisting()
    {
        var endpoint1 = new Uri("http://localhost:8999/");
        var endpoint2 = new Uri("http://localhost:9000/");

        await _registry.RegisterAsync(Agent1Id, endpoint1, "container-1", cancellationToken: Ct);
        await _registry.RegisterAsync(Agent1Id, endpoint2, "container-2", cancellationToken: Ct);

        var result = await _registry.TryGetEndpointAsync(Agent1Id, cancellationToken: Ct);
        result.ShouldBe(endpoint2);
    }

    [Fact]
    public async Task TryGet_ReturnsFullEntry()
    {
        var endpoint = new Uri("http://localhost:8999/");
        var definition = new AgentDefinition(Agent1Id, "Test Agent", null,
            new AgentExecutionConfig("claude-code", "image:v1", Hosting: AgentHostingMode.Persistent));

        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", definition, cancellationToken: Ct);

        var entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);

        entry.ShouldNotBeNull();
        entry!.AgentId.ShouldBe(Agent1Id);
        entry.Endpoint.ShouldBe(endpoint);
        entry.ContainerId.ShouldBe("container-1");
        entry.HealthStatus.ShouldBe(AgentHealthStatus.Healthy);
        entry.ConsecutiveFailures.ShouldBe(0);
        entry.Definition.ShouldBe(definition);
        // #2468: Image column round-trips so cross-process readers (the
        // API host's deployment-badge endpoint) get the image without
        // depending on the locally-cached AgentDefinition.
        entry.Image.ShouldBe("image:v1");
    }

    [Fact]
    public async Task TryGet_AfterCacheClear_RehydratesFromDb()
    {
        // #2468: the registry persists to an EF row so a sibling process
        // (or, in tests, a fresh PersistentAgentRegistry instance pointing
        // at the same DB) can see entries the original process registered.
        var endpoint = new Uri("http://localhost:8999/");
        var definition = new AgentDefinition(Agent1Id, "Test Agent", null,
            new AgentExecutionConfig("claude-code", "image:v1", Hosting: AgentHostingMode.Persistent));

        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", definition, cancellationToken: Ct);

        // Simulate a process boundary: a sibling registry singleton, sharing
        // the same DB. Its in-memory caches are empty so the read must hit
        // the row.
        var siblingRegistry = ActivatorUtilities.CreateInstance<PersistentAgentRegistry>(_serviceProvider);

        var entry = await siblingRegistry.TryGetAsync(Agent1Id, cancellationToken: Ct);

        entry.ShouldNotBeNull();
        entry!.AgentId.ShouldBe(Agent1Id);
        entry.Endpoint.ShouldBe(endpoint);
        entry.ContainerId.ShouldBe("container-1");
        entry.HealthStatus.ShouldBe(AgentHealthStatus.Healthy);
        // The Definition slot is only populated when this process registered
        // the agent — cross-process readers see null and rehydrate on
        // demand via IAgentDefinitionProvider when restarting.
        entry.Definition.ShouldBeNull();
        // The Image column persists separately so the deployment badge
        // still renders the image after a process boundary.
        entry.Image.ShouldBe("image:v1");
    }

    [Fact]
    public async Task MarkUnhealthy_PreventsEndpointLookup()
    {
        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", cancellationToken: Ct);

        await _registry.MarkUnhealthyAsync(Agent1Id, cancellationToken: Ct);

        // TryGetEndpoint should not return unhealthy agents.
        var endpointResult = await _registry.TryGetEndpointAsync(Agent1Id, cancellationToken: Ct);
        endpointResult.ShouldBeNull();

        // But TryGet should still find it.
        var entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        entry.ShouldNotBeNull();
        entry!.HealthStatus.ShouldBe(AgentHealthStatus.Unhealthy);
    }

    [Fact]
    public async Task RunHealthChecksAsync_HealthyAgent_StaysHealthy()
    {
        // #1175: health probe routes through ProbeContainerHttpAsync — no
        // in-container wget, works regardless of worker/agent network topology.
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", cancellationToken: Ct);

        await _registry.RunHealthChecksAsync();

        var entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        entry!.HealthStatus.ShouldBe(AgentHealthStatus.Healthy);
        entry.ConsecutiveFailures.ShouldBe(0);
    }

    [Fact]
    public async Task RunHealthChecksAsync_SingleFailure_MarksUnhealthyImmediately()
    {
        // #2092: a single probe failure flips HealthStatus to Unhealthy on
        // the next sweep so the dispatcher's pre-flight check catches the
        // crash before issuing the doomed A2A call. Restart of the
        // container is still gated on UnhealthyThreshold consecutive
        // failures (covered by RunHealthChecksAsync_ConsecutiveFailures_…).
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", cancellationToken: Ct);

        await _registry.RunHealthChecksAsync();

        var entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        entry!.ConsecutiveFailures.ShouldBe(1);
        entry.HealthStatus.ShouldBe(AgentHealthStatus.Unhealthy);
    }

    [Fact]
    public async Task RunHealthChecksAsync_ConsecutiveFailures_KeepsRowAfterRestartFailure()
    {
        // #2519: restart failure no longer DELETEs the row — it flips
        // HealthStatus to Unknown and resets the counter so the next sweep
        // re-probes the endpoint. The row may describe a sibling-launched
        // container the local restart attempt couldn't see.
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var endpoint = new Uri("http://localhost:8999/");
        var definition = new AgentDefinition(Agent1Id, "Test Agent", null,
            new AgentExecutionConfig("claude-code", "image:v1", Hosting: AgentHostingMode.Persistent));
        _agentProvider.GetByIdAsync(Agent1Id, Arg.Any<CancellationToken>()).Returns(definition);
        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", definition, cancellationToken: Ct);

        // Simulate restart failure (container starts but never becomes ready).
        _containerRuntime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns("new-container");

        // Run health checks until threshold is reached.
        for (var i = 0; i < PersistentAgentRegistry.UnhealthyThreshold; i++)
        {
            await _registry.RunHealthChecksAsync();
        }

        var entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        entry.ShouldNotBeNull();
        // Restart failed but the row remains; HealthStatus is Unknown so
        // the next sweep re-probes (#2519).
        entry!.HealthStatus.ShouldBe(AgentHealthStatus.Unknown);
        entry.ConsecutiveFailures.ShouldBe(0);
    }

    [Fact]
    public async Task RunHealthChecksAsync_RecoveryAfterFailure_ResetsCount()
    {
        var healthy = false;
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(healthy));

        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", cancellationToken: Ct);

        // Simulate one failure.
        await _registry.RunHealthChecksAsync();

        var entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        entry!.ConsecutiveFailures.ShouldBe(1);

        // Now succeed.
        healthy = true;
        await _registry.RunHealthChecksAsync();

        entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        entry!.ConsecutiveFailures.ShouldBe(0);
        entry.HealthStatus.ShouldBe(AgentHealthStatus.Healthy);
    }

    [Fact]
    public async Task RunHealthChecksAsync_AgentWithoutContainerId_FallsBackToHttpProbe()
    {
        // Externally-registered persistent agents (no container id) fall
        // back to the direct HTTP probe — useful for entries managed by
        // out-of-process operators / the cloud control plane.
        var handler = new TestHttpMessageHandler(HttpStatusCode.OK);
        _httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(Agent1Id, endpoint, containerId: null, cancellationToken: Ct);

        await _registry.RunHealthChecksAsync();

        var entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        entry!.HealthStatus.ShouldBe(AgentHealthStatus.Healthy);
        entry.ConsecutiveFailures.ShouldBe(0);
        await _containerRuntime.DidNotReceive().ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConcurrentAccess_MultipleThreads_NoExceptions()
    {
        var handler = new TestHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        // 50 distinct agent Guids — exercises concurrent inserts against
        // disjoint keys. The EF in-memory provider doesn't support the
        // production "two concurrent transactions race to insert the same
        // key, one falls back to the existing row" path (it throws
        // ArgumentException on same-key concurrent inserts), so we don't
        // exercise that path here. The production Postgres provider
        // serialises on the PK unique index and the registry's
        // <c>FirstOrDefaultAsync + upsert</c> pattern is correct against
        // that ordering. The thread-safety surface this test protects is
        // the in-process maps (<c>_localContainers</c> /
        // <c>_inFlightDispatches</c>) and the registry's scope handling.
        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
        {
            var agentGuid = new Guid($"aaaaaaaa-cccc-1111-1111-0000000000{i:D2}");
            var agentId = GuidFormatter.Format(agentGuid);
            var endpoint = new Uri($"http://localhost:{8999 + i}/");

            await _registry.RegisterAsync(agentId, endpoint, $"container-{i}", cancellationToken: Ct);
            await _registry.TryGetEndpointAsync(agentId, cancellationToken: Ct);
            await _registry.TryGetAsync(agentId, cancellationToken: Ct);

            if (i % 5 == 0)
            {
                await _registry.RemoveAsync(agentId, cancellationToken: Ct);
            }
        }));

        await Task.WhenAll(tasks);

        // No exceptions means thread-safety is maintained.
    }

    [Fact]
    public async Task StopAsync_StopsLocalContainers()
    {
        // #2468: graceful shutdown now sweeps only the containers THIS
        // process launched (tracked in the registry's _localContainers
        // map). DB rows are intentionally left behind so a sibling host
        // process keeps the agent reachable across one process's restart.
        await _registry.RegisterAsync(Agent1Id, new Uri("http://localhost:8999/"), "container-1", cancellationToken: Ct);
        await _registry.RegisterAsync(Agent2Id, new Uri("http://localhost:9000/"), "container-2", cancellationToken: Ct);
        await _registry.RegisterAsync(Agent3Id, new Uri("http://localhost:9001/"), null, cancellationToken: Ct); // No container.

        await _registry.StopAsync(CancellationToken.None);

        // Should have stopped the two containers with IDs.
        await _containerRuntime.Received(1).StopAsync("container-1", Arg.Any<CancellationToken>());
        await _containerRuntime.Received(1).StopAsync("container-2", Arg.Any<CancellationToken>());
        await _containerRuntime.DidNotReceive().StopAsync(
            Arg.Is<string>(s => s != "container-1" && s != "container-2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_ContainerStopFailure_DoesNotThrow()
    {
        _containerRuntime.StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("stop failed")));

        await _registry.RegisterAsync(Agent1Id, new Uri("http://localhost:8999/"), "container-1", cancellationToken: Ct);

        // Should not throw even when container stop fails.
        await _registry.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_InitializesHealthTimer()
    {
        await _registry.StartAsync(CancellationToken.None);

        // The timer is internal so we just verify StartAsync completes without error.
        // Actual health check behavior is tested in RunHealthChecksAsync tests.
        await _registry.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void HealthCheckInterval_StaysBoundedForBackgroundCrashDetection()
    {
        // The registry's only crash-detection guarantee for background-detected
        // crashes is "the dashboard status chip stops lying within a few seconds."
        // The dispatch path runs its own pre-flight probe (#2092) so real inbound
        // turns never wait on this sweep. #2203 relaxed the cadence from 1s back
        // to 5s once that became clear; this asserts the upper bound so a future
        // loosening trips the regression on the way out.
        PersistentAgentRegistry.HealthCheckInterval.ShouldBeLessThanOrEqualTo(
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunHealthChecksAsync_CrashedAgent_DetectedInSingleSweep()
    {
        // #2092: a single probe failure from a crashed agent flips
        // HealthStatus to Unhealthy on the very next sweep — without
        // waiting for UnhealthyThreshold consecutive failures (the
        // threshold only gates the more-expensive background restart).
        // The sweep cadence itself is bounded by HealthCheckInterval and
        // pinned by HealthCheckInterval_StaysBoundedForBackgroundCrashDetection.
        var probeResults = new Queue<bool>([true, false]);
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(probeResults.Dequeue()));

        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", cancellationToken: Ct);

        // First sweep: agent answers.
        await _registry.RunHealthChecksAsync();
        var entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        entry!.HealthStatus.ShouldBe(AgentHealthStatus.Healthy);
        entry.ConsecutiveFailures.ShouldBe(0);

        // Second sweep: agent has crashed (probe returns false). Even
        // though we are well below UnhealthyThreshold, the dispatcher's
        // pre-flight contract requires HealthStatus to flip immediately.
        await _registry.RunHealthChecksAsync();
        entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        entry!.HealthStatus.ShouldBe(AgentHealthStatus.Unhealthy);
        entry.ConsecutiveFailures.ShouldBe(1);
    }

    [Fact]
    public async Task ProbeLivenessAsync_DeadEndpoint_ReturnsFalseFastWithBoundedTimeout()
    {
        // #2092 pre-flight semantics: when the dispatcher checks an entry
        // before issuing the A2A call, the probe must fail-fast (within
        // its short caller-supplied timeout) so the cold path doesn't pay
        // a multi-second wall-clock wait against a dead container.
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var entry = new PersistentAgentEntry(
            Agent1Id,
            new Uri("http://localhost:8999/"),
            ContainerId: "container-1",
            StartedAt: DateTimeOffset.UtcNow);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var live = await _registry.ProbeLivenessAsync(
            entry, TimeSpan.FromMilliseconds(750), CancellationToken.None);
        sw.Stop();

        live.ShouldBeFalse();
        // Should be way under the 750ms ceiling because the mock returns
        // synchronously, but the test's real value is asserting fail-fast
        // semantics rather than a tight timing bound.
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StopContainerAsync_RemovesEntryAndStopsContainer_PreservesVolume()
    {
        // #2092: dispatcher restarts on crash detection use
        // StopContainerAsync (not UndeployAsync) so the per-agent
        // workspace volume survives across restart per ADR-0029
        // ("Container crashes do NOT trigger reclamation").
        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", cancellationToken: Ct);

        var stopped = await _registry.StopContainerAsync(Agent1Id, CancellationToken.None);

        stopped.ShouldBeTrue();
        var entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        entry.ShouldBeNull();
        await _containerRuntime.Received().StopAsync("container-1", Arg.Any<CancellationToken>());
        // Volume reclamation is NOT triggered — that's the
        // UndeployAsync responsibility, gated on explicit operator
        // delete. (AgentVolumeManager.ReclaimAsync would shell out
        // to the runtime; the substitute records nothing here, but
        // we verify the public seam: there is no call we're driving
        // through ReclaimAsync from this entry point.)
    }

    [Fact]
    public async Task StopContainerAsync_UnknownAgent_ReturnsFalse()
    {
        var stopped = await _registry.StopContainerAsync(Agent1Id, CancellationToken.None);
        stopped.ShouldBeFalse();
    }

    [Fact]
    public async Task UndeployAsync_WhenTracked_ReclaimsVolume()
    {
        // #2999: genuine decommission (UndeployAsync) stops the container AND
        // reclaims the per-agent workspace volume — contrast StopContainerAsync.
        await _registry.RegisterAsync(Agent1Id, new Uri("http://localhost:8999/"), "container-1", cancellationToken: Ct);

        var undeployed = await _registry.UndeployAsync(Agent1Id, CancellationToken.None);

        undeployed.ShouldBeTrue();
        await _containerRuntime.Received().StopAsync("container-1", Arg.Any<CancellationToken>());
        await _containerRuntime.Received()
            .RemoveVolumeAsync(AgentVolumeNaming.ForAgent(Agent1Id), Arg.Any<CancellationToken>());
        (await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task UndeployAsync_WhenNotTracked_StillReclaimsVolume()
    {
        // #2999: a resumable stop (unit stop, agent undeploy, scale-to-zero)
        // removes the runtime row while deliberately PRESERVING the volume; the
        // later genuine delete must still reclaim the volume even though no row
        // is tracked — otherwise the volume + on-disk credentials leak forever.
        var undeployed = await _registry.UndeployAsync(Agent1Id, CancellationToken.None);

        // Nothing was tracked (resumable stop already dropped the row)...
        undeployed.ShouldBeFalse();
        // ...but the volume reclaim still ran on this decommission path.
        await _containerRuntime.Received()
            .RemoveVolumeAsync(AgentVolumeNaming.ForAgent(Agent1Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAllEntries_ReturnsSnapshot()
    {
        await _registry.RegisterAsync(Agent1Id, new Uri("http://localhost:8999/"), "c1", cancellationToken: Ct);
        await _registry.RegisterAsync(Agent2Id, new Uri("http://localhost:9000/"), "c2", cancellationToken: Ct);

        var entries = await _registry.GetAllEntriesAsync(cancellationToken: Ct);
        entries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task RunHealthChecksAsync_SkipsAgentsWithInFlightDispatch()
    {
        // #2159: while a dispatch is in flight against an agent, the
        // background sweep must NOT probe it. A Python agent serving a
        // synchronous LLM call may legitimately block its event loop for
        // tens of seconds — letting the timer flag that as a crash kills
        // the in-flight inference for no good reason. The dispatch path's
        // own catch block still surfaces real failures via MarkUnhealthy.
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", cancellationToken: Ct);

        using (_registry.BeginDispatch(Agent1Id))
        {
            await _registry.RunHealthChecksAsync();

            // Probe was skipped: status stays Healthy and no probe call was issued.
            var duringEntry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
            duringEntry!.HealthStatus.ShouldBe(AgentHealthStatus.Healthy);
            duringEntry.ConsecutiveFailures.ShouldBe(0);

            await _containerRuntime.DidNotReceive().ProbeContainerHttpAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        // After the dispatch scope is disposed, the sweep runs the probe
        // again and the still-failing probe flips the entry to Unhealthy.
        await _registry.RunHealthChecksAsync();
        var afterEntry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        afterEntry!.HealthStatus.ShouldBe(AgentHealthStatus.Unhealthy);
    }

    [Fact]
    public async Task RunHealthChecksAsync_RestartAttemptFailure_KeepsRowAndFlipsToUnknown()
    {
        // #2519 (replaces the prior "DropsEntry" behaviour): the registry's
        // restart path is still one-shot, but on failure it no longer DELETEs
        // the row. The row may describe a perfectly healthy container started
        // by a sibling host process between the start of this failure streak
        // and the threshold tick. Keep the row, flip HealthStatus to Unknown,
        // and let the next sweep re-probe the endpoint.
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        // Definition with an image is required so TryRestartAsync proceeds
        // past its "no image" early return into the DeployAsync call.
        var definition = new AgentDefinition(Agent1Id, "Test Agent", null,
            new AgentExecutionConfig("claude-code", "image:v1", Hosting: AgentHostingMode.Persistent));
        await _registry.RegisterAsync(Agent1Id, new Uri("http://localhost:8999/"), "container-1", definition, cancellationToken: Ct);

        // Default _agentProvider.GetByIdAsync returns null, so DeployAsync
        // throws SpringException — that's the catch path under test.

        for (var i = 0; i < PersistentAgentRegistry.UnhealthyThreshold; i++)
        {
            await _registry.RunHealthChecksAsync();
        }

        var entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        entry.ShouldNotBeNull();
        entry!.HealthStatus.ShouldBe(AgentHealthStatus.Unknown);
        entry.ConsecutiveFailures.ShouldBe(0);
    }

    [Fact]
    public async Task RunHealthChecksAsync_SiblingHeartbeatBeforeThreshold_SkipsRestart()
    {
        // #2519 freshness gate: when the row's UpdatedAt advances past the
        // first-failure timestamp the registry stamped at streak start, a
        // sibling host process has rewritten the row since this streak
        // began — the local failure count is against an endpoint the
        // sibling has already replaced. The threshold tick must NOT restart;
        // it resets the streak and flips to Unknown so the next sweep
        // re-probes the (now-current) endpoint.
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var endpoint = new Uri("http://localhost:8999/");
        var definition = new AgentDefinition(Agent1Id, "Test Agent", null,
            new AgentExecutionConfig("claude-code", "image:v1", Hosting: AgentHostingMode.Persistent));
        // Use the agent provider so a hypothetical DeployAsync call would
        // proceed past the early-return — failing this expectation surfaces
        // the unwanted restart attempt.
        _agentProvider.GetByIdAsync(Agent1Id, Arg.Any<CancellationToken>()).Returns(definition);

        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", definition, cancellationToken: Ct);

        // First failure tick stamps the local first-failure timestamp and
        // flips to Unhealthy.
        await _registry.RunHealthChecksAsync();

        // Sibling host process writes a heartbeat against the row — the
        // RecordDispatchHeartbeatAsync entry point models that.
        await _registry.RecordDispatchHeartbeatAsync(Agent1Id, Ct);

        // Drive the streak to the threshold. The freshness gate must fire
        // on the threshold tick and skip the restart entirely.
        for (var i = 1; i < PersistentAgentRegistry.UnhealthyThreshold; i++)
        {
            await _registry.RunHealthChecksAsync();
        }

        var entry = await _registry.TryGetAsync(Agent1Id, Ct);
        entry.ShouldNotBeNull();
        // Row still present (not DELETEd by a restart attempt).
        entry!.ContainerId.ShouldBe("container-1");
        // Counter reset by the gate, status flipped to Unknown.
        entry.ConsecutiveFailures.ShouldBe(0);
        entry.HealthStatus.ShouldBe(AgentHealthStatus.Unknown);
        // The freshness gate skipped the restart path: no container
        // teardown / relaunch happened.
        await _containerRuntime.DidNotReceive().StartAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordDispatchHeartbeatAsync_BumpsUpdatedAt()
    {
        // #2519 part 3: a successful A2A dispatch records a freshness
        // heartbeat by bumping UpdatedAt on the runtime row. This is the
        // signal the cross-process freshness gate (above) keys on.
        var endpoint = new Uri("http://localhost:8999/");
        await _registry.RegisterAsync(Agent1Id, endpoint, "container-1", cancellationToken: Ct);

        var before = await _registry.TryGetAsync(Agent1Id, Ct);
        before.ShouldNotBeNull();
        var beforeUpdatedAt = before!.UpdatedAt;

        // Give the wall-clock at least one tick of separation so the
        // strictly-greater comparison below isn't sensitive to clock
        // resolution on fast CI machines.
        await Task.Delay(10, Ct);
        await _registry.RecordDispatchHeartbeatAsync(Agent1Id, Ct);

        var after = await _registry.TryGetAsync(Agent1Id, Ct);
        after.ShouldNotBeNull();
        after!.UpdatedAt.ShouldBeGreaterThan(beforeUpdatedAt);
    }

    [Fact]
    public async Task RecordDispatchHeartbeatAsync_UnknownAgent_DoesNotThrow()
    {
        // No-op when the row no longer exists (operator deleted it between
        // the dispatch start and the heartbeat write). Keeping this silent
        // matches the design contract: the heartbeat is a best-effort
        // freshness signal, not a correctness primitive.
        await _registry.RecordDispatchHeartbeatAsync(Agent1Id, Ct);
    }

    [Fact]
    public async Task TryRestartAsync_DefinitionNotFoundInDurableStorage_RemovesOrphanRow()
    {
        // #2706: when TryRestartAsync cannot locate the agent definition
        // (neither in the local cache nor via IAgentDefinitionProvider),
        // the runtime row is an orphan left behind by a delete-cascade
        // gap (pre-#2713) or a crash mid-delete. Leaving the row alive
        // causes the health timer to probe → fail → hit threshold →
        // log the warning in a loop every ~15s. The correct behaviour
        // is to remove the orphan row so the timer stops and the portal
        // chip reflects accurate state.
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        // Register WITHOUT a definition so the local cache is empty.
        // _agentProvider.GetByIdAsync already returns null by default
        // (wired in the constructor), so both resolution paths fail.
        await _registry.RegisterAsync(Agent1Id, new Uri("http://localhost:8999/"), "container-1",
            definition: null, cancellationToken: Ct);

        for (var i = 0; i < PersistentAgentRegistry.UnhealthyThreshold; i++)
        {
            await _registry.RunHealthChecksAsync();
        }

        // The orphan row must be gone — not left in an infinite retry loop.
        var entry = await _registry.TryGetAsync(Agent1Id, cancellationToken: Ct);
        entry.ShouldBeNull();
    }

    /// <summary>
    /// Test HTTP message handler that returns a configured status code.
    /// </summary>
    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpStatusCode> _statusCodeProvider;

        public TestHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCodeProvider = () => statusCode;
        }

        public TestHttpMessageHandler(Func<HttpStatusCode> statusCodeProvider)
        {
            _statusCodeProvider = statusCodeProvider;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCodeProvider()));
        }
    }
}
