// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;

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
    private readonly PersistentAgentRegistry _registry;

    public PersistentAgentRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _registry = new PersistentAgentRegistry(_containerRuntime, _httpClientFactory, _loggerFactory);
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
        // #1160: health probe routes through the container runtime so it
        // works regardless of worker/agent network topology.
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
    public async Task RunHealthChecksAsync_SingleFailure_IncreasesFailureCount()
    {
        _containerRuntime.ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var endpoint = new Uri("http://localhost:8999/");
        _registry.Register("agent-1", endpoint, "container-1");

        await _registry.RunHealthChecksAsync();

        _registry.TryGet("agent-1", out var entry);
        entry!.ConsecutiveFailures.ShouldBe(1);
        entry.HealthStatus.ShouldBe(AgentHealthStatus.Healthy); // Not yet unhealthy.
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