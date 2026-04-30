// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentVolumeManager"/> (D3c — ADR-0029).
/// Verifies provisioning, reclamation, metrics emission, and mount-string
/// construction without a real container runtime.
/// </summary>
public class AgentVolumeManagerTests
{
    private readonly IContainerRuntime _runtime = Substitute.For<IContainerRuntime>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly AgentVolumeManager _manager;

    public AgentVolumeManagerTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _manager = new AgentVolumeManager(_runtime, _loggerFactory);
    }

    // ── EnsureAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureAsync_CallsRuntimeEnsureVolume()
    {
        var volumeName = await _manager.EnsureAsync("agent-x", TestContext.Current.CancellationToken);

        await _runtime.Received(1).EnsureVolumeAsync(volumeName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureAsync_ReturnsVolumeNameDerivedFromAgentId()
    {
        var volumeName = await _manager.EnsureAsync("my-agent", TestContext.Current.CancellationToken);

        volumeName.ShouldBe("spring-ws-my-agent");
    }

    [Fact]
    public async Task EnsureAsync_CalledTwiceForSameAgent_ReturnsConsistentName()
    {
        var first = await _manager.EnsureAsync("agent-dup", TestContext.Current.CancellationToken);
        var second = await _manager.EnsureAsync("agent-dup", TestContext.Current.CancellationToken);

        first.ShouldBe(second);
    }

    // ── ReclaimAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ReclaimAsync_CallsRuntimeRemoveVolume()
    {
        await _manager.EnsureAsync("agent-reclaim", TestContext.Current.CancellationToken);

        await _manager.ReclaimAsync("agent-reclaim", TestContext.Current.CancellationToken);

        await _runtime.Received(1).RemoveVolumeAsync(
            "spring-ws-agent-reclaim", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReclaimAsync_RuntimeFailure_DoesNotThrow()
    {
        _runtime.RemoveVolumeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("simulated remove failure"));

        await Should.NotThrowAsync(() =>
            _manager.ReclaimAsync("agent-failing", TestContext.Current.CancellationToken));
    }

    // ── RecordVolumeMetricsAsync ───────────────────────────────────────────

    [Fact]
    public async Task RecordVolumeMetricsAsync_NoTrackedVolumes_SkipsRuntimeQuery()
    {
        await _manager.RecordVolumeMetricsAsync(TestContext.Current.CancellationToken);

        await _runtime.DidNotReceive().GetVolumeMetricsAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordVolumeMetricsAsync_AfterEnsure_QueriesMetrics()
    {
        _runtime.GetVolumeMetricsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VolumeMetrics(SizeBytes: 1024L, LastWrite: DateTimeOffset.UtcNow));

        await _manager.EnsureAsync("agent-metrics", TestContext.Current.CancellationToken);
        await _manager.RecordVolumeMetricsAsync(TestContext.Current.CancellationToken);

        await _runtime.Received(1).GetVolumeMetricsAsync(
            "spring-ws-agent-metrics", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordVolumeMetricsAsync_AfterReclaim_NoLongerQueriesReclaimedVolume()
    {
        _runtime.GetVolumeMetricsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VolumeMetrics(SizeBytes: 512L, LastWrite: null));

        await _manager.EnsureAsync("agent-gone", TestContext.Current.CancellationToken);
        await _manager.ReclaimAsync("agent-gone", TestContext.Current.CancellationToken);
        await _manager.RecordVolumeMetricsAsync(TestContext.Current.CancellationToken);

        // RemoveVolumeAsync was called during ReclaimAsync (mock may fail so ignore)
        await _runtime.DidNotReceive().GetVolumeMetricsAsync(
            "spring-ws-agent-gone", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordVolumeMetricsAsync_RuntimeFailureOnMetrics_DoesNotThrow()
    {
        _runtime.GetVolumeMetricsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("metrics timeout"));

        await _manager.EnsureAsync("agent-metrics-fail", TestContext.Current.CancellationToken);

        await Should.NotThrowAsync(() =>
            _manager.RecordVolumeMetricsAsync(TestContext.Current.CancellationToken));
    }

    // ── BuildVolumeMount ──────────────────────────────────────────────────

    [Fact]
    public void BuildVolumeMount_ReturnsColonSeparatedNameAndPath()
    {
        var mount = AgentVolumeManager.BuildVolumeMount("spring-ws-abc");

        mount.ShouldBe($"spring-ws-abc:{AgentVolumeManager.WorkspaceMountPath}");
    }

    [Fact]
    public void BuildVolumeMount_MountPathEndsWithSlash()
    {
        var mount = AgentVolumeManager.BuildVolumeMount("vol");

        mount.ShouldEndWith("/");
    }

    // ── Constants ─────────────────────────────────────────────────────────

    [Fact]
    public void WorkspaceMountPath_IsSpringWorkspace()
    {
        AgentVolumeManager.WorkspaceMountPath.ShouldBe("/spring/workspace/");
    }

    [Fact]
    public void WorkspacePathEnvVar_IsSpringWorkspacePath()
    {
        AgentVolumeManager.WorkspacePathEnvVar.ShouldBe("SPRING_WORKSPACE_PATH");
    }

    // ── StopAsync teardown safety (Flake A — #1354) ───────────────────────

    /// <summary>
    /// Verifies that <see cref="AgentVolumeManager.StopAsync"/> does not throw
    /// when a timer-triggered metrics callback is still in flight at teardown time.
    ///
    /// Arrange: register a volume so the callback has work to do, then
    /// substitute <c>GetVolumeMetricsAsync</c> with an implementation that
    /// blocks on a <see cref="TaskCompletionSource"/> to simulate a slow
    /// container-runtime call still executing when <c>StopAsync</c> is called.
    ///
    /// Act: start the host, manually trigger a metrics sweep via
    /// <see cref="AgentVolumeManager.RunMetricsCallbackAsync"/> (which manages
    /// the in-flight counter the same way the timer does), then call
    /// <c>StopAsync</c> while the callback is still blocked, and release the
    /// blocker concurrently.
    ///
    /// Assert: <c>StopAsync</c> completes without throwing and the callback
    /// drains cleanly after the blocker is released.
    /// </summary>
    [Fact]
    public async Task StopAsync_WithInFlightMetricsCallback_DoesNotThrow()
    {
        // Arrange — a slow GetVolumeMetricsAsync that blocks until we release it.
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _runtime.GetVolumeMetricsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async (_) =>
            {
                callbackStarted.TrySetResult();
                await callbackBlocker.Task;
                return (VolumeMetrics?)new VolumeMetrics(SizeBytes: 0L, LastWrite: null);
            });

        // Register a volume so the metrics sweep actually calls the runtime.
        await _manager.EnsureAsync("agent-teardown-test", TestContext.Current.CancellationToken);

        // Start the hosted service so the timer and counter are initialised.
        await _manager.StartAsync(TestContext.Current.CancellationToken);

        // Manually trigger an in-flight callback via RunMetricsCallbackAsync,
        // which increments the _metricsCallbacksInFlight counter and calls
        // RecordVolumeMetricsAsync — exactly what the timer does. This allows
        // the test to control entry/exit without waiting for a real timer tick.
        var callbackTask = Task.Run(_manager.RunMetricsCallbackAsync, TestContext.Current.CancellationToken);

        // Wait for the callback to confirm it has entered GetVolumeMetricsAsync
        // (i.e. the "in-flight" state we want StopAsync to drain correctly).
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Act — StopAsync must not throw even though the callback is blocked.
        // Release the blocker concurrently so the StopAsync drain loop can finish.
        var stopTask = _manager.StopAsync(TestContext.Current.CancellationToken);
        callbackBlocker.TrySetResult();

        // Assert — neither StopAsync nor the callback task should throw.
        await Should.NotThrowAsync(() => stopTask);
        await Should.NotThrowAsync(() => callbackTask);
    }

    /// <summary>
    /// Verifies that <see cref="AgentVolumeManager.StopAsync"/> does not throw
    /// when called on a manager that was never started (timer is null).
    /// Protects against the NRE regression described in #1354.
    /// </summary>
    [Fact]
    public async Task StopAsync_WhenNeverStarted_DoesNotThrow()
    {
        // Arrange — fresh manager, StartAsync never called.
        var neverStarted = new AgentVolumeManager(_runtime, _loggerFactory);

        // Act + Assert
        await Should.NotThrowAsync(() => neverStarted.StopAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that <see cref="AgentVolumeManager.StopAsync"/> is idempotent:
    /// calling it a second time after the timer has already been disposed does
    /// not throw. Defends against double-dispose in test harness teardown.
    /// </summary>
    [Fact]
    public async Task StopAsync_CalledTwice_DoesNotThrow()
    {
        // Arrange
        await _manager.StartAsync(TestContext.Current.CancellationToken);

        // Act — first StopAsync
        await _manager.StopAsync(TestContext.Current.CancellationToken);

        // Act + Assert — second StopAsync on already-stopped manager
        await Should.NotThrowAsync(() => _manager.StopAsync(TestContext.Current.CancellationToken));
    }
}