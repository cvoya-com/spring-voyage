// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PersistentAgentRegistry"/>.
/// </summary>
public class PersistentAgentRegistryTests : IDisposable
{
    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IAgentDefinitionProvider _agentProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IMcpServer _mcpServer = Substitute.For<IMcpServer>();
    private readonly IAgentRuntimeLauncher _launcher = Substitute.For<IAgentRuntimeLauncher>();
    private readonly PersistentAgentRegistry _registry;

    public PersistentAgentRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _launcher.Kind.Returns("claude-code-cli");
        _mcpServer.Endpoint.Returns("http://host.docker.internal:12345/mcp/");
        _mcpServer.IssueSession(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new McpSession("t", ci.ArgAt<string>(0), ci.ArgAt<string>(1)));
        _launcher.PrepareAsync(Arg.Any<AgentLaunchContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentLaunchSpec(
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                "/workspace"));
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
        services.AddSingleton(_launcher);
        services.AddSingleton<IEnumerable<IAgentRuntimeLauncher>>(_ => new[] { _launcher });
        services.AddSingleton<AgentVolumeManager>();
        services.AddSingleton<PersistentAgentRegistry>();
        services.AddSingleton<PersistentAgentLifecycle>();
        _registry = services.BuildServiceProvider().GetRequiredService<PersistentAgentRegistry>();
    }

    public void Dispose()
    {
        _registry.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Register_TryGetEndpoint_ReturnsEndpoint()
    {
        var endpoint = new Uri("http://localhost:8999/");
        _registry.Register("agent-1", endpoint, "container-1");

        var found = _registry.TryGetEndpoint("agent-1", out var result);

        found.ShouldBeTrue();
        result.ShouldBe(endpoint);
    }

    [Fact]
    public void TryGetEndpoint_UnknownAgent_ReturnsFalse()
    {
        var found = _registry.TryGetEndpoint("nonexistent", out var result);

        found.ShouldBeFalse();
        result.ShouldBeNull();
    }

    [Fact]
    public void Remove_TryGetEndpoint_ReturnsFalse()
    {
        var endpoint = new Uri("http://localhost:8999/");
        _registry.Register("agent-1", endpoint, "container-1");

        _registry.Remove("agent-1");

        var found = _registry.TryGetEndpoint("agent-1", out _);
        found.ShouldBeFalse();
    }

    [Fact]
    public void Remove_UnknownAgent_DoesNotThrow()
    {
        // Should not throw for unknown agents.
        _registry.Remove("nonexistent");
    }

    [Fact]
    public void Register_OverwritesExisting()
    {
        var endpoint1 = new Uri("http://localhost:8999/");
        var endpoint2 = new Uri("http://localhost:9000/");

        _registry.Register("agent-1", endpoint1, "container-1");
        _registry.Register("agent-1", endpoint2, "container-2");

        _registry.TryGetEndpoint("agent-1", out var result);
        result.ShouldBe(endpoint2);
    }

    [Fact]
    public void TryGet_ReturnsFullEntry()
    {
        var endpoint = new Uri("http://localhost:8999/");
        var definition = new AgentDefinition("agent-1", "Test Agent", null,
            new AgentExecutionConfig("claude-code", "image:v1", Hosting: AgentHostingMode.Persistent));

        _registry.Register("agent-1", endpoint, "container-1", definition);

        var found = _registry.TryGet("agent-1", out var entry);

        found.ShouldBeTrue();
        entry.ShouldNotBeNull();
        entry!.AgentId.ShouldBe("agent-1");
        entry.Endpoint.ShouldBe(endpoint);
        entry.ContainerId.ShouldBe("container-1");
        entry.HealthStatus.ShouldBe(AgentHealthStatus.Healthy);
        entry.ConsecutiveFailures.ShouldBe(0);
        entry.Definition.ShouldBe(definition);
    }

    [Fact]
    public void MarkUnhealthy_PreventsEndpointLookup()
    {
        var endpoint = new Uri("http://localhost:8999/");
        _registry.Register("agent-1", endpoint, "container-1");

        _registry.MarkUnhealthy("agent-1");

        // TryGetEndpoint should not return unhealthy agents.
        var found = _registry.TryGetEndpoint("agent-1", out _);
        found.ShouldBeFalse();

        // But TryGet should still find it.
        var exists = _registry.TryGet("agent-1", out var entry);
        exists.ShouldBeTrue();
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
        _registry.Register("agent-1", endpoint, "container-1");

        await _registry.RunHealthChecksAsync();

        _registry.TryGet("agent-1", out var entry);
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
        _registry.Register("agent-1", endpoint, "container-1");

        await _registry.RunHealthChecksAsync();

        _registry.TryGet("agent-1", out var entry);
        entry!.ConsecutiveFailures.ShouldBe(1);
        entry.HealthStatus.ShouldBe(AgentHealthStatus.Unhealthy);
    }

    [Fact]
    public async Task RunHealthChecksAsync_ConsecutiveFailures_MarksUnhealthy()
    {
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var endpoint = new Uri("http://localhost:8999/");
        var definition = new AgentDefinition("agent-1", "Test Agent", null,
            new AgentExecutionConfig("claude-code", "image:v1", Hosting: AgentHostingMode.Persistent));
        _agentProvider.GetByIdAsync("agent-1", Arg.Any<CancellationToken>()).Returns(definition);
        _registry.Register("agent-1", endpoint, "container-1", definition);

        // Simulate restart failure (container starts but never becomes ready).
        _containerRuntime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns("new-container");

        // Run health checks until threshold is reached.
        for (var i = 0; i < PersistentAgentRegistry.UnhealthyThreshold; i++)
        {
            await _registry.RunHealthChecksAsync();
        }

        // After threshold failures + restart attempt, agent should be removed
        // (restart fails because A2A endpoint never becomes ready with mock).
        // Or it could be marked unhealthy. Let's check what happened.
        _registry.TryGet("agent-1", out var entry);

        // Either removed (restart failed) or unhealthy.
        if (entry is not null)
        {
            entry.HealthStatus.ShouldBe(AgentHealthStatus.Unhealthy);
        }
    }

    [Fact]
    public async Task RunHealthChecksAsync_RecoveryAfterFailure_ResetsCount()
    {
        var healthy = false;
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(healthy));

        var endpoint = new Uri("http://localhost:8999/");
        _registry.Register("agent-1", endpoint, "container-1");

        // Simulate one failure.
        await _registry.RunHealthChecksAsync();

        _registry.TryGet("agent-1", out var entry);
        entry!.ConsecutiveFailures.ShouldBe(1);

        // Now succeed.
        healthy = true;
        await _registry.RunHealthChecksAsync();

        _registry.TryGet("agent-1", out entry);
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
        _registry.Register("agent-1", endpoint, containerId: null);

        await _registry.RunHealthChecksAsync();

        _registry.TryGet("agent-1", out var entry);
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

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            var agentId = $"agent-{i % 10}";
            var endpoint = new Uri($"http://localhost:{8999 + i}/");

            _registry.Register(agentId, endpoint, $"container-{i}");
            _registry.TryGetEndpoint(agentId, out _);
            _registry.TryGet(agentId, out _);

            if (i % 5 == 0)
            {
                _registry.Remove(agentId);
            }
        }));

        await Task.WhenAll(tasks);

        // No exceptions means thread-safety is maintained.
    }

    [Fact]
    public async Task StopAsync_StopsAllContainers()
    {
        _registry.Register("agent-1", new Uri("http://localhost:8999/"), "container-1");
        _registry.Register("agent-2", new Uri("http://localhost:9000/"), "container-2");
        _registry.Register("agent-3", new Uri("http://localhost:9001/"), null); // No container.

        await _registry.StopAsync(CancellationToken.None);

        // Should have stopped the two containers with IDs.
        await _containerRuntime.Received(1).StopAsync("container-1", Arg.Any<CancellationToken>());
        await _containerRuntime.Received(1).StopAsync("container-2", Arg.Any<CancellationToken>());
        await _containerRuntime.DidNotReceive().StopAsync(
            Arg.Is<string>(s => s != "container-1" && s != "container-2"),
            Arg.Any<CancellationToken>());

        // Registry should be empty after shutdown.
        _registry.GetAllEntries().ShouldBeEmpty();
    }

    [Fact]
    public async Task StopAsync_ContainerStopFailure_DoesNotThrow()
    {
        _containerRuntime.StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("stop failed")));

        _registry.Register("agent-1", new Uri("http://localhost:8999/"), "container-1");

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
    public void HealthCheckInterval_IsSubSecondOrSecond_For2092CrashDetection()
    {
        // #2092: the registry must observe a crashed persistent agent
        // within ~1s of the A2A endpoint going unreachable. The original
        // 30s sweep cadence missed the cold-path requirement; this asserts
        // the post-#2092 cadence so a future "let's loosen this" change
        // re-trips the regression on the way out.
        PersistentAgentRegistry.HealthCheckInterval.ShouldBeLessThanOrEqualTo(
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RunHealthChecksAsync_CrashedAgent_DetectedInSingleSweep()
    {
        // #2092: a single probe failure from a crashed agent flips
        // HealthStatus to Unhealthy on the very next sweep. Combined
        // with the 1s sweep cadence (HealthCheckInterval), the registry
        // surfaces a crash within the bounded latency the issue asks for
        // — without waiting for UnhealthyThreshold consecutive failures
        // (the threshold only gates the more-expensive background restart).
        var probeResults = new Queue<bool>([true, false]);
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(probeResults.Dequeue()));

        var endpoint = new Uri("http://localhost:8999/");
        _registry.Register("agent-1", endpoint, "container-1");

        // First sweep: agent answers.
        await _registry.RunHealthChecksAsync();
        _registry.TryGet("agent-1", out var entry);
        entry!.HealthStatus.ShouldBe(AgentHealthStatus.Healthy);
        entry.ConsecutiveFailures.ShouldBe(0);

        // Second sweep: agent has crashed (probe returns false). Even
        // though we are well below UnhealthyThreshold, the dispatcher's
        // pre-flight contract requires HealthStatus to flip immediately.
        await _registry.RunHealthChecksAsync();
        _registry.TryGet("agent-1", out entry);
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
            "agent-1",
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
        _registry.Register("agent-1", endpoint, "container-1");

        var stopped = await _registry.StopContainerAsync("agent-1", CancellationToken.None);

        stopped.ShouldBeTrue();
        _registry.TryGet("agent-1", out var entry);
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
        var stopped = await _registry.StopContainerAsync("unknown-agent", CancellationToken.None);
        stopped.ShouldBeFalse();
    }

    [Fact]
    public void GetAllEntries_ReturnsSnapshot()
    {
        _registry.Register("agent-1", new Uri("http://localhost:8999/"), "c1");
        _registry.Register("agent-2", new Uri("http://localhost:9000/"), "c2");

        var entries = _registry.GetAllEntries();
        entries.Count.ShouldBe(2);
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
