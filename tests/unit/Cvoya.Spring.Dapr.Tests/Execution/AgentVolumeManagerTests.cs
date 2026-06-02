// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IAgentBootstrapAuthStore _bootstrapAuthStore = Substitute.For<IAgentBootstrapAuthStore>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly AgentVolumeManager _manager;

    public AgentVolumeManagerTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _manager = new AgentVolumeManager(_runtime, _bootstrapAuthStore, _loggerFactory);
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
    public async Task ReclaimAsync_RevokesBootstrapToken()
    {
        // ADR-0055 §8: bootstrap-token lifetime = agent lifetime. Reclaim
        // (called on undeploy / ephemeral completion) must revoke the
        // token so a stale token cannot pull the bundle of a re-issued
        // agent with the same id.
        await _manager.EnsureAsync("agent-token", TestContext.Current.CancellationToken);

        await _manager.ReclaimAsync("agent-token", TestContext.Current.CancellationToken);

        _bootstrapAuthStore.Received(1).Revoke("agent-token");
    }

    [Fact]
    public async Task ReclaimAsync_RuntimeFailure_DoesNotThrow()
    {
        _runtime.RemoveVolumeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("simulated remove failure"));

        await Should.NotThrowAsync(() =>
            _manager.ReclaimAsync("agent-failing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReclaimAsync_TransientRemoveFailureThenSuccess_RetriesUntilGone()
    {
        // #3005: a reclaim that races the container teardown fails the first
        // RemoveVolumeAsync (e.g. dispatcher 500 — container not fully gone),
        // then succeeds on a retry. The manager must retry rather than leak.
        const string volumeName = "spring-ws-agent-retry";
        var removed = false;
        var attempts = 0;

        _runtime.RemoveVolumeAsync(volumeName, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return Task.FromException(new InvalidOperationException("container still tearing down (500)"));
                }
                removed = true;
                return Task.CompletedTask;
            });

        // The volume is still present until the retry actually removes it.
        _runtime.ListVolumesAsync(volumeName, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<string>>(
                removed ? Array.Empty<string>() : new[] { volumeName }));

        await _manager.ReclaimAsync("agent-retry", TestContext.Current.CancellationToken);

        await _runtime.Received(2).RemoveVolumeAsync(volumeName, Arg.Any<CancellationToken>());
        removed.ShouldBeTrue();
    }

    // ── ReconcileOrphanVolumesAsync (#3005) ────────────────────────────────

    [Fact]
    public async Task ReconcileOrphanVolumesAsync_RemovesOnlyVolumesWithNoLiveDefinition()
    {
        // #3005 + #2999 guard: the reconciler reclaims a deleted agent's leaked
        // volume (no definition, no runtime row) but MUST preserve volumes whose
        // agent/unit definition still exists — including a resumably-stopped
        // agent (definition present, runtime row removed by the stop). GC'ing on
        // "no runtime row" would re-wipe stopped agents' memory (#2999).
        var (manager, scopeFactory, sp) = BuildManagerWithDb();
        using var _ = sp;

        var liveAgentId = new Guid("11110000-0000-0000-0000-000000000001");
        var liveUnitId = new Guid("11110000-0000-0000-0000-000000000002");
        var stoppedAgentId = new Guid("11110000-0000-0000-0000-000000000003");
        var orphanAgentId = new Guid("11110000-0000-0000-0000-000000000004");

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.AgentDefinitions.Add(NewAgent(liveAgentId, "live-agent"));
            db.AgentDefinitions.Add(NewAgent(stoppedAgentId, "stopped-agent")); // definition present, NO runtime row
            db.UnitDefinitions.Add(NewUnit(liveUnitId, "live-unit"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var liveAgentVol = AgentVolumeNaming.ForAgent(GuidFormatter.Format(liveAgentId));
        var liveUnitVol = AgentVolumeNaming.ForAgent(GuidFormatter.Format(liveUnitId));
        var stoppedVol = AgentVolumeNaming.ForAgent(GuidFormatter.Format(stoppedAgentId));
        var orphanVol = AgentVolumeNaming.ForAgent(GuidFormatter.Format(orphanAgentId));

        _runtime.ListVolumesAsync(AgentVolumeNaming.Prefix, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(
                new[] { liveAgentVol, liveUnitVol, stoppedVol, orphanVol }));

        await manager.ReconcileOrphanVolumesAsync(TestContext.Current.CancellationToken);

        // Only the deleted-agent orphan is reclaimed.
        await _runtime.Received(1).RemoveVolumeAsync(orphanVol, Arg.Any<CancellationToken>());
        // Live agent + unit are kept.
        await _runtime.DidNotReceive().RemoveVolumeAsync(liveAgentVol, Arg.Any<CancellationToken>());
        await _runtime.DidNotReceive().RemoveVolumeAsync(liveUnitVol, Arg.Any<CancellationToken>());
        // The resumably-stopped agent's volume is preserved — the #2999 guard.
        await _runtime.DidNotReceive().RemoveVolumeAsync(stoppedVol, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileOrphanVolumesAsync_NoScopeFactory_IsNoOp()
    {
        // The default _manager has no IServiceScopeFactory, so the reconciler is
        // disabled — it must not even enumerate volumes, let alone delete any.
        _runtime.ListVolumesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { "spring-ws-anything" }));

        await _manager.ReconcileOrphanVolumesAsync(TestContext.Current.CancellationToken);

        await _runtime.DidNotReceive().ListVolumesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _runtime.DidNotReceive().RemoveVolumeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
    public void BuildVolumeMount_ReturnsColonSeparatedNameAndPerMemberPath()
    {
        // ADR-0055 §5: the mount path is per-member —
        // /spring/members/<agentId>/.
        var mount = AgentVolumeManager.BuildVolumeMount("spring-ws-abc", "agent-abc");

        mount.ShouldBe("spring-ws-abc:/spring/members/agent-abc/");
    }

    [Fact]
    public void BuildVolumeMount_MountPathEndsWithSlash()
    {
        var mount = AgentVolumeManager.BuildVolumeMount("vol", "any-agent");

        mount.ShouldEndWith("/");
    }

    // ── Constants ─────────────────────────────────────────────────────────

    [Fact]
    public void WorkspacePathEnvVar_IsSpringWorkspacePath()
    {
        AgentVolumeManager.WorkspacePathEnvVar.ShouldBe("SPRING_WORKSPACE_PATH");
    }

    // ── Reconciler helpers ────────────────────────────────────────────────

    private (AgentVolumeManager Manager, IServiceScopeFactory ScopeFactory, ServiceProvider Sp) BuildManagerWithDb()
    {
        var services = new ServiceCollection();
        // Capture the db name OUTSIDE the options lambda: the lambda runs once
        // per DbContext instance, so a fresh Guid inside it would give every
        // scope a different in-memory store (seed + reconcile would not share).
        var dbName = $"AgentVolumeManagerTests-{Guid.NewGuid():N}";
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var manager = new AgentVolumeManager(_runtime, _bootstrapAuthStore, _loggerFactory, scopeFactory);
        return (manager, scopeFactory, sp);
    }

    private static AgentDefinitionEntity NewAgent(Guid id, string name) => new()
    {
        Id = id,
        TenantId = Guid.NewGuid(),
        DisplayName = name,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static UnitDefinitionEntity NewUnit(Guid id, string name) => new()
    {
        Id = id,
        TenantId = Guid.NewGuid(),
        DisplayName = name,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

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
        var neverStarted = new AgentVolumeManager(_runtime, _bootstrapAuthStore, _loggerFactory);

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
