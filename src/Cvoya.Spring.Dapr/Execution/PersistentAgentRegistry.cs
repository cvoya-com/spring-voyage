// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Collections.Concurrent;
using System.Diagnostics;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tracks running persistent agent services, monitors their health via A2A
/// Agent Card probes, and restarts unhealthy agents automatically. The
/// dispatcher reuses registered endpoints across invocations instead of
/// starting a new container per dispatch.
/// </summary>
/// <remarks>
/// <para>
/// Backed by the <c>persistent_agent_runtime</c> EF table (#2468) so the
/// API and worker host processes share a single view of "this agent is
/// up, here is its endpoint / container / health." Before #2468 the
/// registry was an in-memory <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// per host process — the worker's auto-deploy path wrote to its own copy
/// and the API endpoints read from theirs, so the portal's "Persistent
/// deployment" badge surfaced <c>Not deployed</c> for any agent the
/// worker auto-deployed via inbound message.
/// </para>
///
/// <para>
/// Implements <see cref="IHostedService"/> to run a periodic background
/// health-check timer and to stop containers this process started on
/// graceful shutdown. Cross-process container teardown is intentionally
/// out of scope: <see cref="StopAsync"/> only sweeps containers tracked
/// in this process's <see cref="_localContainers"/> set. The DB rows
/// remain until <see cref="UndeployAsync"/> or
/// <see cref="StopContainerAsync"/> is called against the agent.
/// </para>
/// </remarks>
public class PersistentAgentRegistry(
    IContainerRuntime containerRuntime,
    IHttpClientFactory httpClientFactory,
    ContainerLifecycleManager containerLifecycleManager,
    AgentVolumeManager volumeManager,
    IServiceScopeFactory serviceScopeFactory,
    ILoggerFactory loggerFactory) : IHostedService, IDisposable
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PersistentAgentRegistry>();
    private readonly ContainerLifecycleManager _containerLifecycle = containerLifecycleManager;
    private readonly IServiceScopeFactory _scopeFactory = serviceScopeFactory;

    /// <summary>
    /// Set of (agentId → containerId) pairs whose container this process
    /// launched. Used by <see cref="StopAsync"/> to know which containers
    /// to tear down on graceful shutdown without trespassing into containers
    /// other host processes launched. The map is populated on
    /// <see cref="RegisterAsync"/> and cleared on
    /// <see cref="RemoveAsync"/> / <see cref="UndeployAsync"/> /
    /// <see cref="StopContainerAsync"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, LocalContainer> _localContainers = new();

    // Number of concurrent in-flight A2A dispatches per agent. Incremented by
    // BeginDispatch and decremented by DispatchScope.Dispose. The background
    // health timer skips agents with InFlight > 0 (#2159): an agent that's
    // actively serving an A2A call may block its own internal event loop for
    // tens of seconds (e.g. a Python agent with a synchronous LLM call), and
    // restarting the container under that load kills the in-flight inference
    // for no good reason. Real dispatch failures are still detected and
    // marked unhealthy by the catch block in A2AExecutionDispatcher.
    private readonly ConcurrentDictionary<string, int> _inFlightDispatches = new();

    /// <summary>
    /// Timestamp of the first probe failure in the current failure streak,
    /// per agent. Cleared when the next probe succeeds (counter goes back
    /// to 0) and when the restart catch block runs. Used by
    /// <see cref="RunHealthChecksAsync"/> to gate the threshold restart on
    /// row freshness: if the EF row's <c>UpdatedAt</c> has moved forward
    /// since this streak started, a sibling host process rewrote the row
    /// (e.g. the worker's <c>A2AExecutionDispatcher</c> deployed a fresh
    /// container) and our cached failure count is against an endpoint that
    /// no longer exists. The local counter is reset and the restart is
    /// skipped — the next sweep probes the (now-current) endpoint (#2519).
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstFailureAt = new();

    /// <summary>
    /// Cached <see cref="AgentDefinition"/> snapshot for every agent this
    /// process registered locally (i.e. it launched the container). Used
    /// by <see cref="TryRestartAsync"/> to look up the image / runtime /
    /// model without paying a provider round-trip on the slow-path
    /// background restart. The map is the only piece of registry state
    /// that does not survive across processes; cross-process restart
    /// recovery rehydrates the definition via
    /// <see cref="IAgentDefinitionProvider"/> on demand.
    /// </summary>
    private readonly ConcurrentDictionary<string, AgentDefinition> _localDefinitions = new();

    private Timer? _healthTimer;
    private int _healthCheckRunning;

    /// <summary>
    /// Interval between health-check sweeps. The dispatch path already runs a
    /// pre-flight liveness probe (<see cref="DispatchPreflightProbeTimeout"/>),
    /// so a real inbound turn never waits for this sweep — the only thing the
    /// sweep buys is "how fast does a status chip on a dashboard reflect
    /// reality when no traffic is in flight" plus "the catch-up restart for
    /// an agent that crashed under zero load." 5s meets both goals: a single
    /// probe failure still flips <see cref="PersistentAgentEntry.HealthStatus"/>
    /// to <see cref="AgentHealthStatus.Unhealthy"/> on the very next tick
    /// (#2092), and steady-state probe cost drops 5× from the previous 1s
    /// setting. Originally 30s (pre-#2092); tightened to 1s by #2092;
    /// relaxed to 5s by #2203 once the dispatch-time pre-flight made the
    /// sub-second cadence unnecessary. A re-entrancy guard prevents
    /// overlapping ticks when the probe set takes longer than the interval.
    /// </summary>
    internal static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Number of consecutive health-check failures before the registry
    /// attempts a background restart of the agent. The
    /// <see cref="PersistentAgentEntry.HealthStatus"/> is flipped to
    /// <see cref="AgentHealthStatus.Unhealthy"/> on the very first failure
    /// (#2092) so a subsequent dispatch can route through the auto-restart
    /// path before issuing the doomed A2A call; the threshold here only
    /// gates the more expensive container teardown + relaunch, so a single
    /// transient blip does not flap the container.
    /// </summary>
    internal const int UnhealthyThreshold = 3;

    /// <summary>
    /// Timeout for a single health-probe HTTP request.
    /// </summary>
    internal static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Diagnostics-only tag identifying this host process. Stored on the
    /// row's <c>owner_host</c> column so an operator looking at a stale
    /// "Not deployed" can immediately see which process registered the
    /// agent. Never used for routing or correctness decisions.
    /// </summary>
    private static readonly string OwnerHostTag = BuildOwnerHostTag();

    /// <summary>
    /// Registers or updates a persistent agent service. The row is the
    /// cross-process source of truth (#2468); the in-process maps below
    /// are diagnostics-only.
    /// </summary>
    /// <param name="agentId">The agent identifier (canonical Guid wire form).</param>
    /// <param name="endpoint">The A2A endpoint URL of the running agent service.</param>
    /// <param name="containerId">The container identifier, if applicable.</param>
    /// <param name="definition">The agent definition, kept locally for restart on the slow-path background timer.</param>
    /// <param name="sidecarId">The Dapr sidecar container id (when applicable).</param>
    /// <param name="sidecarNetworkName">Per-deployment network name (when applicable).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task RegisterAsync(
        string agentId,
        Uri endpoint,
        string? containerId,
        AgentDefinition? definition = null,
        string? sidecarId = null,
        string? sidecarNetworkName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(endpoint);

        var agentGuid = ParseAgentId(agentId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.PersistentAgentRuntime
            .FirstOrDefaultAsync(r => r.AgentId == agentGuid, cancellationToken);

        if (row is null)
        {
            row = new PersistentAgentRuntimeEntity { AgentId = agentGuid };
            db.PersistentAgentRuntime.Add(row);
        }

        var now = DateTimeOffset.UtcNow;
        row.Endpoint = endpoint.ToString();
        row.ContainerId = containerId;
        row.StartedAt = now;
        row.HealthStatus = AgentHealthStatus.Healthy;
        row.ConsecutiveFailures = 0;
        row.SidecarId = sidecarId;
        row.SidecarNetworkName = sidecarNetworkName;
        row.Image = definition?.Execution?.Image;
        row.OwnerHost = OwnerHostTag;
        // #2519: PersistentAgentRuntimeEntity opts out of the default
        // audit-pipeline UpdatedAt bump because the cross-process freshness
        // gate must ignore bookkeeping writes (failure-counter increments,
        // MarkUnhealthy flags). RegisterAsync is a fresh "container alive"
        // signal, so we set UpdatedAt explicitly to advance the gate.
        row.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        // #2519: a fresh registration always resets this process's local
        // failure-streak anchor — the row points at a new endpoint we
        // launched, so any prior streak against the previous endpoint is
        // irrelevant.
        _firstFailureAt.TryRemove(agentId, out _);

        // Track which containers this process launched so StopAsync only
        // tears down the right set on graceful shutdown.
        if (containerId is not null)
        {
            _localContainers[agentId] = new LocalContainer(
                containerId, sidecarId, sidecarNetworkName);
        }
        if (definition is not null)
        {
            _localDefinitions[agentId] = definition;
        }

        _logger.LogInformation(
            EventIds.AgentRegistered,
            "Persistent agent {AgentId} registered at {Endpoint} (container {ContainerId})",
            agentId, endpoint, containerId);
    }

    /// <summary>
    /// Attempts to retrieve the A2A endpoint for a running persistent agent.
    /// Only returns healthy or unknown-state agents.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The endpoint when the agent is registered and healthy; <c>null</c> otherwise.</returns>
    public async Task<Uri?> TryGetEndpointAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var entry = await TryGetAsync(agentId, cancellationToken);
        if (entry is null || entry.HealthStatus != AgentHealthStatus.Healthy)
        {
            return null;
        }

        return entry.Endpoint;
    }

    /// <summary>
    /// Attempts to retrieve a running persistent agent entry from the
    /// shared registry row. Returns <c>null</c> when no row exists.
    /// </summary>
    /// <param name="agentId">The agent identifier (canonical Guid wire form).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task<PersistentAgentEntry?> TryGetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var agentGuid = ParseAgentId(agentId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.PersistentAgentRuntime
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.AgentId == agentGuid, cancellationToken);

        return row is null ? null : ToEntry(agentId, row);
    }

    /// <summary>
    /// Removes a persistent agent entry (e.g. after its container was stopped).
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task RemoveAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var agentGuid = ParseAgentId(agentId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.PersistentAgentRuntime
            .FirstOrDefaultAsync(r => r.AgentId == agentGuid, cancellationToken);

        if (row is null)
        {
            return;
        }

        db.PersistentAgentRuntime.Remove(row);
        await db.SaveChangesAsync(cancellationToken);

        _localContainers.TryRemove(agentId, out _);
        _localDefinitions.TryRemove(agentId, out _);
        _firstFailureAt.TryRemove(agentId, out _);

        _logger.LogInformation(EventIds.AgentUnregistered, "Persistent agent {AgentId} unregistered", agentId);
    }

    /// <summary>
    /// Marks the start of an in-flight A2A dispatch against <paramref name="agentId"/>.
    /// Returns an <see cref="IDisposable"/> that decrements the in-flight count
    /// on dispose; while the count is greater than zero the background health
    /// timer skips probes for this agent (#2159). The dispatch path remains
    /// responsible for its own failure detection — if the A2A call fails the
    /// catch block in <c>A2AExecutionDispatcher</c> still calls
    /// <see cref="MarkUnhealthyAsync"/>.
    /// </summary>
    /// <param name="agentId">The agent whose dispatch is starting.</param>
    public IDisposable BeginDispatch(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        _inFlightDispatches.AddOrUpdate(agentId, 1, (_, v) => v + 1);
        return new DispatchScope(this, agentId);
    }

    private void EndDispatch(string agentId)
    {
        _inFlightDispatches.AddOrUpdate(agentId, 0, (_, v) => v <= 1 ? 0 : v - 1);
    }

    internal bool HasInFlightDispatch(string agentId) =>
        _inFlightDispatches.TryGetValue(agentId, out var count) && count > 0;

    private sealed class DispatchScope : IDisposable
    {
        private readonly PersistentAgentRegistry _registry;
        private readonly string _agentId;
        private int _disposed;

        public DispatchScope(PersistentAgentRegistry registry, string agentId)
        {
            _registry = registry;
            _agentId = agentId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _registry.EndDispatch(_agentId);
            }
        }
    }

    /// <summary>
    /// Marks an agent as unhealthy so it will be restarted on the next health sweep.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="containerId">
    /// When provided, the mark is applied only if the registry row still tracks this
    /// specific container. A stale container ID (e.g. from a concurrent restart that
    /// already replaced the container) must not poison the new container's health state.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task MarkUnhealthyAsync(string agentId, string? containerId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var agentGuid = ParseAgentId(agentId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.PersistentAgentRuntime
            .FirstOrDefaultAsync(r => r.AgentId == agentGuid, cancellationToken);

        if (row is null)
        {
            return;
        }

        if (containerId is not null && row.ContainerId != containerId)
        {
            return;
        }

        row.HealthStatus = AgentHealthStatus.Unhealthy;
        row.ConsecutiveFailures = UnhealthyThreshold;
        // #2519: UpdatedAt is intentionally not modified here. A
        // MarkUnhealthy flag is not a fresh "container alive" signal;
        // SpringDbContext.ApplyAuditTimestamps preserves the existing
        // UpdatedAt for PersistentAgentRuntimeEntity Modified writes that
        // don't explicitly set it. The cross-process freshness gate in
        // RunHealthChecksAsync depends on this asymmetry to distinguish
        // sibling heartbeats from bookkeeping writes.
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Returns a snapshot of all registered entries across all tenants
    /// the ambient <c>SpringDbContext</c> resolves. Used by the
    /// persistent-agent lifecycle HTTP surface (<c>spring agent
    /// deploy/status/undeploy</c>, #396) and by tests/diagnostics.
    /// </summary>
    public async Task<IReadOnlyCollection<PersistentAgentEntry>> GetAllEntriesAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var rows = await db.PersistentAgentRuntime
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => ToEntry(GuidFormatter.Format(r.AgentId), r))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Stops (and removes) the backing container for an agent and drops its
    /// registry entry. Used by <c>spring agent undeploy</c> (#396). Returns
    /// <c>true</c> when the agent was tracked and cleaned up, <c>false</c>
    /// when there was nothing to undeploy. Failures to stop the container are
    /// logged but do not prevent the entry from being removed — the operator
    /// intent is "this agent should not be running" and a dangling container
    /// is recoverable via the runtime's own cleanup tools.
    /// </summary>
    /// <remarks>
    /// Per ADR-0029 § "Durable state: a per-agent persistent volume", the
    /// workspace volume is reclaimed when the persistent agent is deleted
    /// (this method). Container crashes do NOT trigger reclamation — the
    /// health-check loop restarts the container and the volume survives.
    /// </remarks>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task<bool> UndeployAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var agentGuid = ParseAgentId(agentId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.PersistentAgentRuntime
            .FirstOrDefaultAsync(r => r.AgentId == agentGuid, cancellationToken);

        if (row is null)
        {
            return false;
        }

        db.PersistentAgentRuntime.Remove(row);
        await db.SaveChangesAsync(cancellationToken);

        var entry = ToEntry(agentId, row);
        _localContainers.TryRemove(agentId, out _);
        _localDefinitions.TryRemove(agentId, out _);
        _firstFailureAt.TryRemove(agentId, out _);

        if (entry.ContainerId is not null)
        {
            await TeardownOrStopEntryAsync(entry, cancellationToken);
        }

        _logger.LogInformation(
            EventIds.AgentUnregistered,
            "Persistent agent {AgentId} undeployed (container {ContainerId})",
            agentId, entry.ContainerId);

        // Reclaim the workspace volume on agent delete.
        await volumeManager.ReclaimAsync(agentId, CancellationToken.None);

        return true;
    }

    /// <summary>
    /// Stops and removes the backing container for an agent and drops its
    /// registry entry — without reclaiming the per-agent workspace volume.
    /// Used by the dispatch path's pre-flight crash detection (#2092):
    /// when a persistent agent crashes mid-flight, the volume must survive
    /// across the restart per ADR-0029 § "Durable state: a per-agent
    /// persistent volume". <see cref="UndeployAsync"/> remains the
    /// agent-delete entry point and reclaims the volume.
    /// </summary>
    /// <remarks>
    /// Idempotent: returns <c>false</c> when nothing is tracked. Failures
    /// to stop the container are logged but do not prevent the entry from
    /// being removed — the operator intent is "this entry is no longer
    /// authoritative" and a dangling container is recoverable via the
    /// runtime's own cleanup tools or the next dispatch's relaunch.
    /// </remarks>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task<bool> StopContainerAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var agentGuid = ParseAgentId(agentId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.PersistentAgentRuntime
            .FirstOrDefaultAsync(r => r.AgentId == agentGuid, cancellationToken);

        if (row is null)
        {
            return false;
        }

        db.PersistentAgentRuntime.Remove(row);
        await db.SaveChangesAsync(cancellationToken);

        var entry = ToEntry(agentId, row);
        _localContainers.TryRemove(agentId, out _);
        _localDefinitions.TryRemove(agentId, out _);
        _firstFailureAt.TryRemove(agentId, out _);

        if (entry.ContainerId is not null)
        {
            await TeardownOrStopEntryAsync(entry, cancellationToken);
        }

        _logger.LogInformation(
            EventIds.AgentUnregistered,
            "Persistent agent {AgentId} container stopped for restart (container {ContainerId}; volume preserved)",
            agentId, entry.ContainerId);

        return true;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(EventIds.HealthMonitorStarting, "Persistent agent health monitor starting");
        // Re-entrancy guard: when a probe sweep takes longer than the
        // tightened 1s interval (#2092) the timer thread would otherwise
        // start a second concurrent sweep. The Interlocked check below
        // skips the tick if the previous one is still running so probe
        // calls don't pile up against a slow / hung dispatcher channel.
        _healthTimer = new Timer(
            callback: _ =>
            {
                if (Interlocked.CompareExchange(ref _healthCheckRunning, 1, 0) != 0)
                {
                    return;
                }

                _ = RunHealthChecksAsync().ContinueWith(
                    _ => Interlocked.Exchange(ref _healthCheckRunning, 0),
                    TaskScheduler.Default);
            },
            state: null,
            dueTime: HealthCheckInterval,
            period: HealthCheckInterval);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(EventIds.GracefulShutdown, "Persistent agent registry shutting down — stopping local containers");

        if (_healthTimer is not null)
        {
            await _healthTimer.DisposeAsync();
            _healthTimer = null;
        }

        // Graceful shutdown only sweeps the containers THIS process started.
        // The DB rows themselves are not deleted; sibling host processes may
        // still be using their own containers and the next health probe in
        // any process will flip the row to Unhealthy if the container goes
        // away under traffic. Cross-process teardown is intentionally out
        // of scope (#2468).
        var stopTasks = _localContainers
            .ToArray()
            .Select(kvp => TeardownLocalContainerAsync(kvp.Value, cancellationToken));

        await Task.WhenAll(stopTasks);
        _localContainers.Clear();
        _localDefinitions.Clear();
        _firstFailureAt.Clear();
    }

    /// <summary>
    /// Runs health checks against all registered agents. Called on the timer thread.
    /// </summary>
    /// <remarks>
    /// Issue #2092: a single probe failure flips
    /// <see cref="PersistentAgentEntry.HealthStatus"/> to
    /// <see cref="AgentHealthStatus.Unhealthy"/> immediately so a subsequent
    /// dispatch routes through the auto-restart path instead of issuing a
    /// doomed A2A call. The expensive container teardown + relaunch is
    /// still gated on <see cref="UnhealthyThreshold"/> consecutive failures
    /// to avoid restart-flapping on a transient probe blip — the dispatch
    /// path will trigger an explicit restart on the next inbound turn if
    /// the agent stays down before the threshold is reached.
    /// </remarks>
    internal async Task RunHealthChecksAsync()
    {
        var entries = await GetAllEntriesAsync();
        if (entries.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Running health checks for {Count} persistent agent(s)", entries.Count);

        foreach (var entry in entries)
        {
            // #2159: skip the probe while a dispatch is in flight. The
            // probe goes through `podman run --network container:<id>
            // curl ...` which depends on the agent's own HTTP server being
            // responsive — a Python agent that has blocked its asyncio loop
            // on a slow LLM call (or any agent legitimately busy serving a
            // request) will fail the probe even though it is doing exactly
            // what it should be. Letting the timer drive a restart under
            // those conditions kills the in-flight inference. Real failures
            // are still observed: the dispatch path's own A2A call surfaces
            // any RPC error via the catch block in A2AExecutionDispatcher,
            // which calls MarkUnhealthy with the live container id.
            if (HasInFlightDispatch(entry.AgentId))
            {
                _logger.LogDebug(
                    "Skipping background health probe for agent {AgentId} — dispatch in flight",
                    entry.AgentId);
                continue;
            }

            try
            {
                var healthy = await ProbeHealthAsync(entry);

                if (healthy)
                {
                    // Reset failure count and freshness anchor on success.
                    _firstFailureAt.TryRemove(entry.AgentId, out _);
                    if (entry.ConsecutiveFailures > 0 || entry.HealthStatus != AgentHealthStatus.Healthy)
                    {
                        await UpdateHealthAsync(entry.AgentId, AgentHealthStatus.Healthy, 0, CancellationToken.None);
                    }
                }
                else
                {
                    await OnProbeFailureAsync(entry, exception: null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check failed for agent {AgentId}", entry.AgentId);
                await OnProbeFailureAsync(entry, exception: ex);
            }
        }
    }

    /// <summary>
    /// Per-attempt timeout used by the dispatcher's pre-flight liveness
    /// probe (#2092). Shorter than <see cref="HealthProbeTimeout"/> because
    /// this runs on the cold path of every persistent dispatch — a healthy
    /// agent must pay only a few-hundred-ms ceiling, not the multi-second
    /// background-loop budget. A crashed container fails the probe in
    /// sub-100ms (TCP RST), so the small timeout is rarely the binding
    /// factor.
    /// </summary>
    internal static readonly TimeSpan DispatchPreflightProbeTimeout =
        TimeSpan.FromSeconds(5);

    /// <summary>
    /// Probes the A2A Agent Card endpoint to verify the agent is healthy.
    /// </summary>
    /// <remarks>
    /// When the entry carries a container id, the probe is dispatched via
    /// <see cref="IContainerRuntime.ProbeContainerHttpAsync"/>, which the
    /// dispatcher implements as <c>podman exec &lt;id&gt; curl …</c> inside
    /// the agent's network namespace — the only mechanism ADR 0028 Decision A
    /// allows for crossing into a tenant container without joining its
    /// network. Agent images install <c>curl</c> explicitly. Entries without
    /// a container id (legacy externally-registered persistent agents) fall
    /// back to a direct host-side HTTP probe via <c>HttpClient</c>.
    /// </remarks>
    internal Task<bool> ProbeHealthAsync(PersistentAgentEntry entry)
        => ProbeLivenessAsync(entry, HealthProbeTimeout, CancellationToken.None);

    /// <summary>
    /// Probes the A2A Agent Card endpoint with a caller-supplied timeout.
    /// Used by both the periodic background sweep
    /// (<see cref="HealthProbeTimeout"/>) and the dispatcher's pre-flight
    /// liveness check (<see cref="DispatchPreflightProbeTimeout"/>) so the
    /// probe surface stays in one place. Returns <c>false</c> on timeout,
    /// connection failure, non-2xx, or any exception thrown by the
    /// underlying probe transport.
    /// </summary>
    /// <param name="entry">The agent entry whose endpoint to probe.</param>
    /// <param name="timeout">Per-attempt timeout cap for this probe.</param>
    /// <param name="cancellationToken">Outer cancellation token (typically the dispatch turn's token).</param>
    public async Task<bool> ProbeLivenessAsync(
        PersistentAgentEntry entry,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var agentCardUri = new Uri(entry.Endpoint, ".well-known/agent.json").ToString();

        if (!string.IsNullOrEmpty(entry.ContainerId))
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(timeout);
            try
            {
                return await containerRuntime.ProbeContainerHttpAsync(
                    entry.ContainerId, agentCardUri, probeCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
        }

        using var httpClient = httpClientFactory.CreateClient("PersistentAgentHealthCheck");
        httpClient.Timeout = timeout;

        try
        {
            var response = await httpClient.GetAsync(agentCardUri, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Records the outcome of a failing probe tick for <paramref name="entry"/>.
    /// Stamps the local first-failure timestamp on the first observed failure
    /// in a streak, flips the row to Unhealthy, increments
    /// <see cref="PersistentAgentEntry.ConsecutiveFailures"/>, and — when the
    /// streak reaches <see cref="UnhealthyThreshold"/> — gates the restart
    /// attempt on row freshness (#2519) before dispatching to
    /// <see cref="TryRestartAsync"/>.
    /// </summary>
    /// <param name="entry">The agent whose probe failed.</param>
    /// <param name="exception">
    /// The probe exception, if the failure surfaced as a thrown exception
    /// rather than a clean <c>false</c> return. Used only for logging.
    /// </param>
    private async Task OnProbeFailureAsync(PersistentAgentEntry entry, Exception? exception)
    {
        var failures = entry.ConsecutiveFailures + 1;

        // Stamp first-failure timestamp on streak start so the threshold
        // restart can gate on row freshness below. Re-stamps after a
        // successful probe cleared the entry are handled by the success
        // branch in RunHealthChecksAsync.
        _firstFailureAt.TryAdd(entry.AgentId, DateTimeOffset.UtcNow);

        // #2092: flip to Unhealthy on the first failure so the dispatcher's
        // pre-flight check on the next dispatch can route through the
        // auto-restart path before the doomed A2A call goes out.
        await UpdateHealthAsync(entry.AgentId, AgentHealthStatus.Unhealthy, failures, CancellationToken.None);

        if (failures == 1 && exception is null)
        {
            _logger.LogInformation(
                EventIds.AgentUnhealthy,
                "Agent {AgentId} probe failed; marked Unhealthy after first failure (restart at threshold {Threshold})",
                entry.AgentId, UnhealthyThreshold);
        }

        if (failures < UnhealthyThreshold)
        {
            return;
        }

        // #2519: before tearing down and relaunching, check whether the EF
        // row has been rewritten since this failure streak started. The
        // worker host's A2AExecutionDispatcher writes a fresh row when it
        // auto-deploys an agent on inbound message and bumps UpdatedAt on
        // a successful A2A POST; without this gate the API host's
        // accumulated failure count against the previous endpoint clobbers
        // the sibling's just-launched container.
        var freshEntry = await TryGetAsync(entry.AgentId, CancellationToken.None);
        if (freshEntry is not null
            && _firstFailureAt.TryGetValue(entry.AgentId, out var firstFailureAt)
            && freshEntry.UpdatedAt > firstFailureAt)
        {
            _logger.LogInformation(
                EventIds.AgentRestarting,
                "Agent {AgentId} hit restart threshold but the runtime row was rewritten by a sibling " +
                "process at {RowUpdatedAt} after this streak began at {FirstFailureAt}; resetting " +
                "local failure count and skipping restart this tick (#2519).",
                entry.AgentId, freshEntry.UpdatedAt, firstFailureAt);

            _firstFailureAt.TryRemove(entry.AgentId, out _);
            await UpdateHealthAsync(entry.AgentId, AgentHealthStatus.Unknown, 0, CancellationToken.None);
            return;
        }

        _logger.LogWarning(
            EventIds.AgentUnhealthy,
            "Agent {AgentId} hit restart threshold after {Failures} consecutive failures; attempting restart",
            entry.AgentId, failures);

        // Background restart on the slow path. The dispatch path still gets
        // first crack via its pre-flight check (#2092) so a real inbound
        // turn doesn't wait for this loop's threshold to elapse.
        await TryRestartAsync(entry);
    }

    /// <summary>
    /// Bumps the EF row's <c>UpdatedAt</c> column without touching any other
    /// state. Called by <see cref="A2AExecutionDispatcher"/> on a successful
    /// A2A POST so a sibling host's restart path (gated on row freshness in
    /// <see cref="RunHealthChecksAsync"/>) sees the heartbeat and skips an
    /// otherwise-scheduled restart against an endpoint a busy agent is
    /// happily serving (#2519).
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task RecordDispatchHeartbeatAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var agentGuid = ParseAgentId(agentId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.PersistentAgentRuntime
            .FirstOrDefaultAsync(r => r.AgentId == agentGuid, cancellationToken);

        if (row is null)
        {
            return;
        }

        // The audit-timestamp pipeline in SpringDbContext stamps UpdatedAt
        // on every Modified row; we mark the entity Modified via an unchanged
        // re-assignment so the audit hook fires even though no logical column
        // changed. This is the cheap heartbeat per-issue #2519 recommendation.
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateHealthAsync(
        string agentId,
        AgentHealthStatus status,
        int consecutiveFailures,
        CancellationToken cancellationToken)
    {
        var agentGuid = ParseAgentId(agentId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.PersistentAgentRuntime
            .FirstOrDefaultAsync(r => r.AgentId == agentGuid, cancellationToken);

        if (row is null)
        {
            return;
        }

        row.HealthStatus = status;
        row.ConsecutiveFailures = consecutiveFailures;
        // #2519: UpdatedAt is intentionally not modified here. Probe
        // bookkeeping writes (failure counter increments, success-branch
        // "Healthy/0" resets, the freshness gate's Unknown flip) must not
        // bump UpdatedAt — the cross-process freshness gate in
        // RunHealthChecksAsync compares the column against the local
        // first-failure timestamp, and a per-tick bump from our own probe
        // loop would defeat it. SpringDbContext.ApplyAuditTimestamps
        // preserves the existing UpdatedAt for PersistentAgentRuntimeEntity
        // Modified writes that don't explicitly set it.
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Attempts to restart an unhealthy agent by stopping the old container
    /// and starting a fresh one. Resolves the agent's
    /// <see cref="AgentDefinition"/> via the local cache when this process
    /// registered the agent, falling back to
    /// <see cref="IAgentDefinitionProvider"/> when the row was registered
    /// by a sibling process (#2468 cross-process recovery).
    /// </summary>
    private async Task TryRestartAsync(PersistentAgentEntry entry)
    {
        _logger.LogInformation(
            EventIds.AgentRestarting,
            "Attempting restart of persistent agent {AgentId}", entry.AgentId);

        try
        {
            using var scope = _scopeFactory.CreateScope();

            // Definition resolution order: local registration cache (the
            // process that ran the deploy keeps the full record), then the
            // agent definition provider (works across processes). When the
            // row came from another process and the provider also can't
            // find a definition, we keep the row but reset the failure
            // count so the timer doesn't keep retrying the same dead path
            // every tick.
            AgentDefinition? definition = null;
            if (!_localDefinitions.TryGetValue(entry.AgentId, out definition))
            {
                var provider = scope.ServiceProvider.GetService<IAgentDefinitionProvider>();
                if (provider is not null)
                {
                    definition = await provider.GetByIdAsync(entry.AgentId, CancellationToken.None);
                }
            }

            if (definition?.Execution?.Image is null)
            {
                _logger.LogWarning(
                    "Cannot restart agent {AgentId}: no definition/image available; keeping as unavailable.",
                    entry.AgentId);
                // Keep the row in the registry so the portal chip stays
                // "unavailable" rather than flipping back to "idle". Reset
                // ConsecutiveFailures so we don't re-enter TryRestartAsync on
                // every subsequent health tick — the cycle naturally rebuilds
                // to the restart threshold over UnhealthyThreshold ticks.
                await UpdateHealthAsync(entry.AgentId, AgentHealthStatus.Unhealthy, 0, CancellationToken.None);
                return;
            }

            var lifecycle = scope.ServiceProvider.GetRequiredService<PersistentAgentLifecycle>();
            await lifecycle.DeployAsync(entry.AgentId, cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart persistent agent {AgentId}", entry.AgentId);
            // #2519: do NOT DELETE the row on restart failure — the row may
            // describe a perfectly healthy container started by a sibling
            // host process between the start of this failure streak and the
            // threshold tick (the original symptom that filed this issue).
            // Flip HealthStatus to Unknown + reset the failure count so the
            // next sweep re-probes the (possibly fresh) endpoint, and clear
            // the local first-failure timestamp so a recovered probe starts
            // a clean streak.
            await UpdateHealthAsync(entry.AgentId, AgentHealthStatus.Unknown, 0, CancellationToken.None);
            _firstFailureAt.TryRemove(entry.AgentId, out _);
        }
    }

    /// <summary>
    /// Waits until the A2A Agent Card endpoint returns 200 or the timeout expires.
    /// </summary>
    /// <remarks>
    /// The probe is dispatched via
    /// <see cref="IContainerRuntime.ProbeContainerHttpAsync"/>, which the
    /// dispatcher implements as <c>podman exec &lt;id&gt; curl …</c> inside
    /// the agent container's own network namespace (ADR 0028 Decision A,
    /// #2198). Agent images install <c>curl</c> explicitly.
    /// </remarks>
    internal async Task<bool> WaitForA2AReadyAsync(
        string containerId,
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var agentCardUri = new Uri(endpoint, ".well-known/agent.json").ToString();

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                if (await containerRuntime.ProbeContainerHttpAsync(containerId, agentCardUri, cts.Token))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(
                    "Readiness probe attempt failed for {Endpoint} (container {ContainerId}): {Reason}",
                    endpoint, containerId, ex.Message);
            }

            try
            {
                await Task.Delay(A2AExecutionDispatcher.ReadinessProbeInterval, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return false;
    }

    private async Task TeardownOrStopEntryAsync(
        PersistentAgentEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.SidecarId is not null
            && entry.SidecarNetworkName is not null
            && entry.ContainerId is not null)
        {
            await _containerLifecycle.TeardownAsync(
                entry.ContainerId, entry.SidecarId, entry.SidecarNetworkName, cancellationToken);
        }
        else if (entry.ContainerId is not null)
        {
            await StopContainerSafeAsync(entry.ContainerId, cancellationToken);
        }
    }

    private async Task TeardownLocalContainerAsync(
        LocalContainer local,
        CancellationToken cancellationToken)
    {
        if (local.SidecarId is not null && local.SidecarNetworkName is not null)
        {
            try
            {
                await _containerLifecycle.TeardownAsync(
                    local.ContainerId, local.SidecarId, local.SidecarNetworkName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to tear down container {ContainerId} on shutdown", local.ContainerId);
            }
        }
        else
        {
            await StopContainerSafeAsync(local.ContainerId, cancellationToken);
        }
    }

    private async Task StopContainerSafeAsync(string containerId, CancellationToken ct)
    {
        try
        {
            await containerRuntime.StopAsync(containerId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop container {ContainerId}", containerId);
        }
    }

    /// <summary>
    /// Translates a persisted row to the in-memory wire record. The
    /// <see cref="PersistentAgentEntry.Definition"/> slot is populated
    /// from the per-process local cache when this host registered the
    /// row; sibling-process readers see <c>null</c> here and rehydrate
    /// the definition via <see cref="IAgentDefinitionProvider"/> on
    /// demand (restart). The <see cref="PersistentAgentEntry.Image"/>
    /// field is populated from the row's <c>image</c> column so
    /// cross-process display reads never depend on the local cache.
    /// </summary>
    private PersistentAgentEntry ToEntry(string agentId, PersistentAgentRuntimeEntity row)
    {
        _localDefinitions.TryGetValue(agentId, out var definition);

        return new PersistentAgentEntry(
            AgentId: agentId,
            Endpoint: new Uri(row.Endpoint),
            ContainerId: row.ContainerId,
            StartedAt: row.StartedAt,
            HealthStatus: row.HealthStatus,
            ConsecutiveFailures: row.ConsecutiveFailures,
            Definition: definition,
            SidecarId: row.SidecarId,
            SidecarNetworkName: row.SidecarNetworkName,
            Image: row.Image,
            UpdatedAt: row.UpdatedAt);
    }

    private static Guid ParseAgentId(string agentId)
    {
        if (!GuidFormatter.TryParse(agentId, out var guid))
        {
            throw new ArgumentException(
                $"Agent id '{agentId}' is not a parseable Guid. " +
                "PersistentAgentRegistry keys must be canonical 32-char no-dash hex Guid strings.",
                nameof(agentId));
        }

        return guid;
    }

    private static string BuildOwnerHostTag()
    {
        try
        {
            var machine = Environment.MachineName;
            using var process = Process.GetCurrentProcess();
            var processName = process.ProcessName;
            return $"{machine}/{processName}/{process.Id}";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _healthTimer?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Event IDs for persistent agent registry logging (range 2240-2259).
    /// </summary>
    private static class EventIds
    {
        public static readonly EventId AgentRegistered = new(2240, nameof(AgentRegistered));
        public static readonly EventId AgentUnregistered = new(2241, nameof(AgentUnregistered));
        public static readonly EventId AgentUnhealthy = new(2242, nameof(AgentUnhealthy));
        public static readonly EventId AgentRestarting = new(2243, nameof(AgentRestarting));
        public static readonly EventId AgentRestarted = new(2244, nameof(AgentRestarted));
        public static readonly EventId HealthMonitorStarting = new(2245, nameof(HealthMonitorStarting));
        public static readonly EventId GracefulShutdown = new(2246, nameof(GracefulShutdown));
    }

    /// <summary>
    /// Tracks the container + sidecar identifiers this process actually
    /// launched so <see cref="StopAsync"/> can tear them down on graceful
    /// shutdown without touching containers owned by sibling host
    /// processes.
    /// </summary>
    private sealed record LocalContainer(string ContainerId, string? SidecarId, string? SidecarNetworkName);
}

/// <summary>
/// Health status of a persistent agent service.
/// </summary>
public enum AgentHealthStatus
{
    /// <summary>The agent is responding to health probes.</summary>
    Healthy,

    /// <summary>The agent has failed consecutive health probes and needs restart.</summary>
    Unhealthy,

    /// <summary>
    /// Health is not currently known to this process — used by the
    /// freshness gate in <see cref="PersistentAgentRegistry.RunHealthChecksAsync"/>
    /// when a sibling host process rewrote the row mid-streak (#2519) and
    /// by <see cref="PersistentAgentRegistry.TryRestartAsync"/> when a
    /// restart attempt failed but the row may describe a sibling-launched
    /// container. The next sweep re-probes the endpoint and flips the
    /// state back to Healthy or Unhealthy.
    /// </summary>
    Unknown
}

/// <summary>
/// Describes a running persistent agent service.
/// </summary>
/// <param name="AgentId">The agent identifier.</param>
/// <param name="Endpoint">The A2A endpoint URL the agent is reachable at.</param>
/// <param name="ContainerId">The container identifier, if the agent runs in a container.</param>
/// <param name="StartedAt">When the agent service was started.</param>
/// <param name="HealthStatus">Current health status.</param>
/// <param name="ConsecutiveFailures">Number of consecutive health-check failures.</param>
/// <param name="Definition">
/// The agent definition, retained for restart. Populated when this
/// process registered the agent locally; <c>null</c> when the entry is
/// rehydrated from a sibling-process write — the restart path then
/// rehydrates the definition via <see cref="IAgentDefinitionProvider"/>.
/// </param>
/// <param name="SidecarId">Dapr sidecar container id (Dapr-sidecar agents only).</param>
/// <param name="SidecarNetworkName">Per-deployment network name when a sidecar network was created.</param>
/// <param name="Image">
/// Container image the agent is running (when known). Mirrors
/// <see cref="AgentDefinition.Execution"/>.<c>Image</c> on the local
/// process and is otherwise rehydrated from the EF row's <c>image</c>
/// column so cross-process readers can render the deployment badge's
/// image field without rehydrating <see cref="Definition"/>.
/// </param>
/// <param name="UpdatedAt">
/// UTC timestamp of the last write to the backing EF row. Bumped by
/// every row write (register, mark-unhealthy, update-health) and by
/// <see cref="PersistentAgentRegistry.RecordDispatchHeartbeatAsync"/>
/// on a successful A2A dispatch. The health-sweep restart path
/// (#2519) gates on this column to detect sibling-host writes between
/// the start of a failure streak and the threshold tick — if the row
/// has been rewritten since this process began counting failures,
/// the count is against an endpoint the sibling has already replaced.
/// </param>
public record PersistentAgentEntry(
    string AgentId,
    Uri Endpoint,
    string? ContainerId,
    DateTimeOffset StartedAt,
    AgentHealthStatus HealthStatus = AgentHealthStatus.Healthy,
    int ConsecutiveFailures = 0,
    AgentDefinition? Definition = null,
    string? SidecarId = null,
    string? SidecarNetworkName = null,
    string? Image = null,
    DateTimeOffset UpdatedAt = default);
