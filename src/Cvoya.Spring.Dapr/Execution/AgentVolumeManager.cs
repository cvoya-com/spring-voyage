// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Provisions and reclaims per-agent workspace volumes (D3c — ADR-0029).
///
/// Each agent receives exactly one named Podman volume. The volume is:
/// <list type="bullet">
///   <item>Created (idempotent) before the agent's container is first started.</item>
///   <item>Mounted at <see cref="WorkspaceMountPath"/> inside every container
///         instance of that agent, using <c>SPRING_WORKSPACE_PATH</c> as the
///         in-container env var.</item>
///   <item>Persistent across container restarts — a crashed container's volume
///         survives so the next instance can resume from checkpoint files.</item>
///   <item>Reclaimed when the agent is deleted (persistent) or when an
///         ephemeral agent declares work done (not on mid-flight crashes).</item>
/// </list>
///
/// Volume-level metrics (size, growth rate) are emitted through the standard
/// <see cref="ILogger"/> telemetry path as structured log entries. The volume's
/// content is never inspected.
/// </summary>
/// <remarks>
/// <para>
/// Volume naming follows <see cref="AgentVolumeNaming.ForAgent"/> — the name
/// is stable across restarts and collision-free across tenants.
/// </para>
/// <para>
/// This class does not supervise the metric-collection loop directly; the
/// caller (host background service or health-check sweep) drives
/// <see cref="RecordVolumeMetricsAsync"/> at whatever cadence it chooses.
/// </para>
/// </remarks>
public class AgentVolumeManager(
    IContainerRuntime containerRuntime,
    ILoggerFactory loggerFactory) : IHostedService
{
    /// <summary>
    /// Canonical mount path inside every agent container. Matches the
    /// <c>SPRING_WORKSPACE_PATH</c> env var value the launchers set and the
    /// recommended default from the D1 spec (§ 2.1 and § 3.1).
    /// </summary>
    public const string WorkspaceMountPath = "/spring/workspace/";

    /// <summary>Env var name the D1 spec mandates for the workspace mount path.</summary>
    public const string WorkspacePathEnvVar = "SPRING_WORKSPACE_PATH";

    private static readonly TimeSpan MetricsInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentVolumeManager>();
    private Timer? _metricsTimer;

    // Tracks the count of timer callbacks currently executing so StopAsync
    // can drain in-flight metric sweeps before disposing the timer. Uses a
    // simple Interlocked counter rather than a SemaphoreSlim to avoid
    // allocation in the steady-state hot path where no teardown is in
    // progress. StopAsync polls with a short Task.Delay rather than a
    // blocking spin so the teardown thread stays cooperative.
    private int _metricsCallbacksInFlight;

    // Track volumes registered during this process lifetime so the metric
    // sweep knows which volumes to query without re-enumerating all Podman
    // volumes (which would be expensive and noisy in a multi-tenant host).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _volumesByAgentId = new();

    /// <summary>
    /// Ensures the per-agent workspace volume exists, creating it if absent.
    /// Idempotent — repeated calls for the same agent are safe.
    /// Returns the volume name for use in the container mount spec.
    /// </summary>
    /// <param name="agentId">The agent's stable identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The named-volume identifier to pass to <c>-v &lt;name&gt;:&lt;path&gt;</c>
    /// when starting the agent container.
    /// </returns>
    public async Task<string> EnsureAsync(string agentId, CancellationToken ct = default)
    {
        var volumeName = AgentVolumeNaming.ForAgent(agentId);

        await containerRuntime.EnsureVolumeAsync(volumeName, ct);

        _volumesByAgentId[agentId] = volumeName;

        _logger.LogInformation(
            EventIds.VolumeProvisioned,
            "Workspace volume {VolumeName} ensured for agent {AgentId}",
            volumeName, agentId);

        return volumeName;
    }

    /// <summary>
    /// Reclaims the workspace volume for an agent. Called on agent delete
    /// (persistent agents) or ephemeral completion. MUST NOT be called for
    /// mid-flight container crashes — the volume must survive those so the
    /// restarted container can resume.
    /// </summary>
    /// <param name="agentId">The agent whose volume is to be reclaimed.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ReclaimAsync(string agentId, CancellationToken ct = default)
    {
        var volumeName = AgentVolumeNaming.ForAgent(agentId);

        _logger.LogInformation(
            EventIds.VolumeReclaiming,
            "Reclaiming workspace volume {VolumeName} for agent {AgentId}",
            volumeName, agentId);

        _volumesByAgentId.TryRemove(agentId, out _);

        try
        {
            await containerRuntime.RemoveVolumeAsync(volumeName, ct);

            _logger.LogInformation(
                EventIds.VolumeReclaimed,
                "Workspace volume {VolumeName} reclaimed for agent {AgentId}",
                volumeName, agentId);
        }
        catch (Exception ex)
        {
            // Reclamation failures are logged but do not block the caller —
            // the agent registry entry is removed regardless. An operator can
            // reclaim orphaned volumes with `podman volume prune`.
            _logger.LogWarning(
                EventIds.VolumeReclaimFailed,
                ex,
                "Failed to reclaim workspace volume {VolumeName} for agent {AgentId}; " +
                "volume may need manual cleanup via `podman volume rm {VolumeName}`",
                volumeName, agentId, volumeName);
        }
    }

    /// <summary>
    /// Records volume-level metrics (size, last-write) for all volumes
    /// tracked by this manager. Emits structured log entries; no content
    /// inspection. Called by the background timer and by tests.
    /// </summary>
    public async Task RecordVolumeMetricsAsync(CancellationToken ct = default)
    {
        var snapshot = _volumesByAgentId.ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        _logger.LogDebug(
            "Collecting volume metrics for {Count} agent workspace volume(s)",
            snapshot.Length);

        foreach (var (agentId, volumeName) in snapshot)
        {
            try
            {
                var metrics = await containerRuntime.GetVolumeMetricsAsync(volumeName, ct);
                if (metrics is null)
                {
                    continue;
                }

                _logger.LogInformation(
                    EventIds.VolumeMetricsRecorded,
                    "Workspace volume metrics: agent={AgentId} volume={VolumeName} size_bytes={SizeBytes} last_write={LastWrite}",
                    agentId, volumeName, metrics.SizeBytes, metrics.LastWrite);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to collect metrics for volume {VolumeName} (agent {AgentId})",
                    volumeName, agentId);
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _metricsTimer = new Timer(
            callback: _ =>
            {
                // Guard: if teardown has already stopped the timer we should
                // not start another sweep even if a queued callback fires
                // after Change(Infinite). The Interlocked increment is still
                // visible to StopAsync's drain loop so it can wait correctly.
                Interlocked.Increment(ref _metricsCallbacksInFlight);
                _ = RunMetricsCallbackAsync();
            },
            state: null,
            dueTime: MetricsInterval,
            period: MetricsInterval);

        return Task.CompletedTask;
    }

    // Internal so Cvoya.Spring.Dapr.Tests can invoke it directly to simulate
    // an in-flight timer callback in teardown-race unit tests (#1354).
    internal async Task RunMetricsCallbackAsync()
    {
        try
        {
            await RecordVolumeMetricsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Exceptions from the timer callback are swallowed here because
            // there is no caller to propagate them to — the fire-and-forget
            // async Task would otherwise silently fault. RecordVolumeMetricsAsync
            // already logs per-volume failures; this catch handles anything
            // that escapes that inner try/catch (e.g. disposed containerRuntime
            // during host teardown).
            _logger.LogWarning(
                ex,
                "Unhandled exception in metrics timer callback; this is expected during host teardown");
        }
        finally
        {
            Interlocked.Decrement(ref _metricsCallbacksInFlight);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_metricsTimer is null)
        {
            return;
        }

        // Step 1: prevent any new timer callbacks from firing. Change to
        // Infinite/Infinite before disposing so a queued-but-not-yet-started
        // callback cannot increment _metricsCallbacksInFlight after we finish
        // the drain loop below.
        try
        {
            _metricsTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Timer already disposed (e.g. redundant StopAsync call) — skip.
        }

        // Step 2: drain in-flight callbacks. Each callback decrements the
        // counter in its finally block, so we poll until it reaches zero.
        // We cap the wait at the host shutdown timeout via cancellationToken
        // to avoid blocking shutdown indefinitely if a callback hangs.
        while (Volatile.Read(ref _metricsCallbacksInFlight) > 0
               && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(millisecondsDelay: 10, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // Step 3: dispose the timer. Wrap in try/catch so a race between two
        // concurrent StopAsync callers (unusual but possible in test harnesses
        // that call DisposeAsync more than once) does not propagate an
        // ObjectDisposedException through the host teardown path and mask the
        // real test assertion.
        try
        {
            await _metricsTimer.DisposeAsync();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or NullReferenceException)
        {
            _logger.LogDebug(
                ex,
                "Metrics timer dispose raced with another dispose call during shutdown; ignored");
        }
        finally
        {
            _metricsTimer = null;
        }
    }

    /// <summary>
    /// Builds the volume-mount string for a container run command.
    /// Format: <c>{volumeName}:{mountPath}</c>.
    /// </summary>
    public static string BuildVolumeMount(string volumeName)
        => $"{volumeName}:{WorkspaceMountPath}";

    /// <summary>
    /// Event IDs for workspace volume management (range 2260–2279, within
    /// the Cvoya.Spring.Dapr.Execution range 2200–2299 from CONVENTIONS.md).
    /// </summary>
    private static class EventIds
    {
        public static readonly EventId VolumeProvisioned = new(2260, nameof(VolumeProvisioned));
        public static readonly EventId VolumeReclaiming = new(2261, nameof(VolumeReclaiming));
        public static readonly EventId VolumeReclaimed = new(2262, nameof(VolumeReclaimed));
        public static readonly EventId VolumeReclaimFailed = new(2263, nameof(VolumeReclaimFailed));
        public static readonly EventId VolumeMetricsRecorded = new(2264, nameof(VolumeMetricsRecorded));
    }
}