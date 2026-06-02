// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Provisions and reclaims per-agent workspace volumes (D3c — ADR-0029).
///
/// Each agent receives exactly one named Podman volume. The volume is:
/// <list type="bullet">
///   <item>Created (idempotent) before the agent's container is first started.</item>
///   <item>Mounted at the per-member path
///         <c>/spring/members/&lt;agentId&gt;/</c> inside every container
///         instance of that agent (ADR-0055 §5), surfaced to the agent
///         process as the <c>SPRING_WORKSPACE_PATH</c> env var.</item>
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
    IAgentBootstrapAuthStore bootstrapAuthStore,
    ILoggerFactory loggerFactory,
    // #3005: the orphan-volume GC reconciler reads agent/unit definitions to
    // build its keep-set. Optional so existing test fixtures that construct the
    // manager with the original three args still compose — when null the
    // reconciler is disabled (no DB access available). Production DI always
    // supplies the built-in IServiceScopeFactory.
    IServiceScopeFactory? scopeFactory = null) : IHostedService
{
    /// <summary>Number of <see cref="ReclaimAsync"/> attempts before giving up
    /// and deferring to the GC reconciler. A container teardown that is still
    /// settling makes the first <c>volume rm</c> fail (dispatcher 500) or defer
    /// ("in use"); a couple of backed-off retries clear the common race (#3005).</summary>
    private const int MaxReclaimAttempts = 3;

    /// <summary>Backoff before the first reclaim retry; doubled each attempt.</summary>
    private static readonly TimeSpan InitialReclaimBackoff = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Env var name the D1 spec mandates for the workspace mount path.
    /// Delegates to <see cref="AgentWorkspaceContract.WorkspacePathEnvVar"/>.
    /// </summary>
    public const string WorkspacePathEnvVar = AgentWorkspaceContract.WorkspacePathEnvVar;

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
    /// Reclaims the workspace volume for an agent. Called only on genuine
    /// decommission — persistent-agent delete (agent delete / unit
    /// force-delete / clean delete) or ephemeral-turn completion. MUST NOT be
    /// called for container crashes, health-restarts, redeploys, scale-to-zero,
    /// or resumable stops (unit stop, agent undeploy): the volume must survive
    /// those so the next instance resumes with its durable memory + session
    /// transcripts intact (ADR-0029; #2999). Restart and resumable-stop
    /// teardown go through
    /// <see cref="PersistentAgentRegistry.StopContainerAsync"/>, which stops the
    /// container but preserves the volume.
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

        // ADR-0055 §8: bootstrap token lifetime = agent lifetime. Revoke on
        // undeploy so a stale token cannot pull the bundle of a re-issued
        // agent with the same id.
        bootstrapAuthStore.Revoke(agentId);

        // #3005: a reclaim that races the container teardown leaks the volume —
        // RemoveVolumeAsync either throws (dispatcher 500 because the container
        // is not fully gone) or defers silently ("in use"). Retry a few times
        // with backoff and confirm the volume is actually gone before declaring
        // success; the GC reconciler is the backstop if it still isn't.
        var backoff = InitialReclaimBackoff;
        for (var attempt = 1; attempt <= MaxReclaimAttempts; attempt++)
        {
            try
            {
                await containerRuntime.RemoveVolumeAsync(volumeName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    EventIds.VolumeReclaimFailed,
                    ex,
                    "Attempt {Attempt}/{Max} to remove workspace volume {VolumeName} (agent {AgentId}) failed: {Message}",
                    attempt, MaxReclaimAttempts, volumeName, agentId, ex.Message);
            }

            // RemoveVolumeAsync may have deferred without throwing (an "in use"
            // race that the runtime warns-and-swallows), so confirm removal.
            if (!await VolumeExistsAsync(volumeName, ct).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    EventIds.VolumeReclaimed,
                    "Workspace volume {VolumeName} reclaimed for agent {AgentId}",
                    volumeName, agentId);
                return;
            }

            if (attempt < MaxReclaimAttempts)
            {
                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff += backoff;
            }
        }

        // Still present after every attempt — do not block the caller (the
        // registry entry is removed regardless). The orphan-volume GC
        // reconciler will reclaim it once the container is gone.
        _logger.LogWarning(
            EventIds.VolumeReclaimFailed,
            "Workspace volume {VolumeName} (agent {AgentId}) still present after {Max} reclaim attempts; " +
            "the orphan-volume GC reconciler will retry. Manual cleanup: `podman volume rm {VolumeName}`",
            volumeName, agentId, MaxReclaimAttempts, volumeName);
    }

    /// <summary>
    /// Returns <c>true</c> when a volume named exactly <paramref name="volumeName"/>
    /// still exists. Uses the volume's own name as the list prefix and checks
    /// for an exact match. Best-effort: a listing failure surfaces as
    /// <c>false</c> (treated as "gone"), deferring any genuine leak to the GC
    /// reconciler rather than spinning the reclaim retry loop.
    /// </summary>
    private async Task<bool> VolumeExistsAsync(string volumeName, CancellationToken ct)
    {
        var matches = await containerRuntime.ListVolumesAsync(volumeName, ct).ConfigureAwait(false);
        return matches is not null && matches.Contains(volumeName, StringComparer.Ordinal);
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

    /// <summary>
    /// Reclaims orphaned <c>spring-ws-*</c> workspace volumes — those with no
    /// live owning agent/unit definition (#3005). A volume leaks when a reclaim
    /// races the container teardown; this background sweep is the durable
    /// backstop (and cleans up volumes leaked by earlier runs). Internal so
    /// tests can drive it directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The orphan criterion is deliberately <b>"no live definition"</b>, NOT
    /// "no <c>persistent_agent_runtime</c> row" as the issue first framed it.
    /// Post-#2999 a <i>resumably-stopped</i> agent has its runtime row removed
    /// while its volume is preserved (durable memory survives the stop); GC'ing
    /// on the absence of a runtime row would re-wipe stopped agents' memory and
    /// regress #2999. Agent/unit definitions cover both stopped and running
    /// agents. The runtime-row set is folded into the keep-set as well — pure
    /// belt-and-suspenders for any entity mid-lifecycle — so a volume is
    /// reclaimed only when <b>neither</b> a definition nor a runtime row
    /// references it.
    /// </para>
    /// <para>
    /// Disabled (no-op) when no <see cref="IServiceScopeFactory"/> was supplied,
    /// and skips the whole sweep on any enumeration failure so a transient
    /// runtime/DB hiccup never triggers a false-positive reclaim.
    /// </para>
    /// </remarks>
    internal async Task ReconcileOrphanVolumesAsync(CancellationToken ct)
    {
        if (scopeFactory is null)
        {
            return;
        }

        IReadOnlyList<string> volumes;
        try
        {
            volumes = await containerRuntime.ListVolumesAsync(AgentVolumeNaming.Prefix, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Orphan-volume reconciler could not list {Prefix}* volumes; skipping this sweep",
                AgentVolumeNaming.Prefix);
            return;
        }

        if (volumes.Count == 0)
        {
            return;
        }

        HashSet<string> keep;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var agentIds = await db.AgentDefinitions.IgnoreQueryFilters()
                .Select(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
            var unitIds = await db.UnitDefinitions.IgnoreQueryFilters()
                .Select(u => u.Id).ToListAsync(ct).ConfigureAwait(false);
            var runtimeIds = await db.PersistentAgentRuntime.AsNoTracking()
                .Select(r => r.AgentId).ToListAsync(ct).ConfigureAwait(false);

            keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in agentIds.Concat(unitIds).Concat(runtimeIds))
            {
                keep.Add(AgentVolumeNaming.ForAgent(GuidFormatter.Format(id)));
            }
        }
        catch (Exception ex)
        {
            // A keep-set we cannot fully build risks a false-positive reclaim,
            // so skip the sweep entirely rather than delete on partial data.
            _logger.LogWarning(
                ex,
                "Orphan-volume reconciler could not load the keep-set; skipping this sweep to avoid a false-positive reclaim");
            return;
        }

        var orphans = volumes.Where(v => !keep.Contains(v)).ToList();
        if (orphans.Count == 0)
        {
            return;
        }

        _logger.LogWarning(
            EventIds.VolumeOrphanReclaiming,
            "Orphan-volume reconciler found {Count} workspace volume(s) with no live agent/unit definition; reclaiming.",
            orphans.Count);

        foreach (var orphan in orphans)
        {
            _logger.LogWarning(
                EventIds.VolumeOrphanReclaiming,
                "Reclaiming orphan workspace volume {VolumeName} (no live agent/unit definition).",
                orphan);
            try
            {
                await containerRuntime.RemoveVolumeAsync(orphan, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    EventIds.VolumeReclaimFailed,
                    ex,
                    "Failed to reclaim orphan workspace volume {VolumeName}; will retry next sweep",
                    orphan);
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
            // #3005: piggyback the volume-metrics cadence to GC orphaned
            // spring-ws-* volumes (e.g. left behind by a reclaim that failed
            // while the container was still tearing down). Self-contained
            // error handling inside, but the outer catch is the safety net.
            await ReconcileOrphanVolumesAsync(CancellationToken.None);
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
    /// Builds the volume-mount string for a container run command using
    /// the per-member path <c>/spring/members/&lt;agentId&gt;/</c>
    /// (ADR-0055 §5). Format: <c>{volumeName}:/spring/members/{agentId}/</c>.
    /// </summary>
    public static string BuildVolumeMount(string volumeName, string agentId)
        => $"{volumeName}:{AgentWorkspaceContract.BuildMountPath(agentId)}";

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
        public static readonly EventId VolumeOrphanReclaiming = new(2265, nameof(VolumeOrphanReclaiming));
    }
}
