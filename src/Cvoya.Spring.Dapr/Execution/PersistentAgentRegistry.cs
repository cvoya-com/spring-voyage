// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Execution;

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
/// Implements <see cref="IHostedService"/> to run a periodic background
/// health-check timer and to stop all tracked containers on graceful shutdown.
/// Thread-safe: all state is stored in a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
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
    private readonly ConcurrentDictionary<string, PersistentAgentEntry> _entries = new();
    // Number of concurrent in-flight A2A dispatches per agent. Incremented by
    // BeginDispatch and decremented by DispatchScope.Dispose. The background
    // health timer skips agents with InFlight > 0 (#2159): an agent that's
    // actively serving an A2A call may block its own internal event loop for
    // tens of seconds (e.g. a Python agent with a synchronous LLM call), and
    // restarting the container under that load kills the in-flight inference
    // for no good reason. Real dispatch failures are still detected and
    // marked unhealthy by the catch block in A2AExecutionDispatcher.
    private readonly ConcurrentDictionary<string, int> _inFlightDispatches = new();
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
    /// Registers or updates a persistent agent service.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="endpoint">The A2A endpoint URL of the running agent service.</param>
    /// <param name="containerId">The container identifier, if applicable.</param>
    /// <param name="definition">The agent definition, needed for restart.</param>
    public void Register(
        string agentId,
        Uri endpoint,
        string? containerId,
        AgentDefinition? definition = null,
        string? sidecarId = null,
        string? sidecarNetworkName = null)
    {
        var entry = new PersistentAgentEntry(
            agentId, endpoint, containerId, DateTimeOffset.UtcNow,
            HealthStatus: AgentHealthStatus.Healthy,
            ConsecutiveFailures: 0,
            Definition: definition,
            SidecarId: sidecarId,
            SidecarNetworkName: sidecarNetworkName);
        _entries[agentId] = entry;

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
    /// <param name="endpoint">The endpoint, if found and healthy.</param>
    /// <returns><c>true</c> if the agent is registered and healthy.</returns>
    public bool TryGetEndpoint(string agentId, out Uri? endpoint)
    {
        if (_entries.TryGetValue(agentId, out var entry) && entry.HealthStatus == AgentHealthStatus.Healthy)
        {
            endpoint = entry.Endpoint;
            return true;
        }

        endpoint = null;
        return false;
    }

    /// <summary>
    /// Attempts to retrieve a running persistent agent entry.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="entry">The entry, if found.</param>
    /// <returns><c>true</c> if the agent is registered.</returns>
    public bool TryGet(string agentId, out PersistentAgentEntry? entry)
    {
        return _entries.TryGetValue(agentId, out entry);
    }

    /// <summary>
    /// Removes a persistent agent entry (e.g. after its container was stopped).
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    public void Remove(string agentId)
    {
        if (_entries.TryRemove(agentId, out _))
        {
            _logger.LogInformation(EventIds.AgentUnregistered, "Persistent agent {AgentId} unregistered", agentId);
        }
    }

    /// <summary>
    /// Marks the start of an in-flight A2A dispatch against <paramref name="agentId"/>.
    /// Returns an <see cref="IDisposable"/> that decrements the in-flight count
    /// on dispose; while the count is greater than zero the background health
    /// timer skips probes for this agent (#2159). The dispatch path remains
    /// responsible for its own failure detection — if the A2A call fails the
    /// catch block in <c>A2AExecutionDispatcher</c> still calls
    /// <see cref="MarkUnhealthy"/>.
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
    /// When provided, the mark is applied only if the registry entry still tracks this
    /// specific container. A stale container ID (e.g. from a concurrent restart that
    /// already replaced the container) must not poison the new container's health state.
    /// </param>
    public void MarkUnhealthy(string agentId, string? containerId = null)
    {
        if (_entries.TryGetValue(agentId, out var entry))
        {
            if (containerId is not null && entry.ContainerId != containerId)
                return;

            _entries[agentId] = entry with
            {
                HealthStatus = AgentHealthStatus.Unhealthy,
                ConsecutiveFailures = UnhealthyThreshold
            };
        }
    }

    /// <summary>
    /// Returns a snapshot of all registered entries. Used by the persistent-
    /// agent lifecycle HTTP surface (<c>spring agent deploy/status/undeploy</c>,
    /// #396) and by tests/diagnostics.
    /// </summary>
    public IReadOnlyCollection<PersistentAgentEntry> GetAllEntries()
    {
        return _entries.Values.ToList().AsReadOnly();
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
        if (!_entries.TryRemove(agentId, out var entry))
        {
            return false;
        }

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
        if (!_entries.TryRemove(agentId, out var entry))
        {
            return false;
        }

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
        _logger.LogInformation(EventIds.GracefulShutdown, "Persistent agent registry shutting down — stopping all containers");

        if (_healthTimer is not null)
        {
            await _healthTimer.DisposeAsync();
            _healthTimer = null;
        }

        var stopTasks = _entries.Values
            .Select(e => TeardownOrStopEntryAsync(e, cancellationToken));

        await Task.WhenAll(stopTasks);
        _entries.Clear();
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
        var entries = _entries.Values.ToList();
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
                    // Reset failure count on success.
                    if (entry.ConsecutiveFailures > 0 || entry.HealthStatus != AgentHealthStatus.Healthy)
                    {
                        _entries[entry.AgentId] = entry with
                        {
                            HealthStatus = AgentHealthStatus.Healthy,
                            ConsecutiveFailures = 0
                        };
                    }
                }
                else
                {
                    var failures = entry.ConsecutiveFailures + 1;

                    // #2092: flip to Unhealthy on the first failure so the
                    // dispatcher's pre-flight check on the next dispatch
                    // can route through the auto-restart path before the
                    // doomed A2A call goes out.
                    _entries[entry.AgentId] = entry with
                    {
                        HealthStatus = AgentHealthStatus.Unhealthy,
                        ConsecutiveFailures = failures,
                    };

                    if (failures == 1)
                    {
                        _logger.LogInformation(
                            EventIds.AgentUnhealthy,
                            "Agent {AgentId} probe failed; marked Unhealthy after first failure (restart at threshold {Threshold})",
                            entry.AgentId, UnhealthyThreshold);
                    }

                    if (failures >= UnhealthyThreshold)
                    {
                        _logger.LogWarning(
                            EventIds.AgentUnhealthy,
                            "Agent {AgentId} hit restart threshold after {Failures} consecutive failures; attempting restart",
                            entry.AgentId, failures);

                        // Background restart on the slow path. The dispatch
                        // path still gets first crack via its pre-flight
                        // check (#2092) so a real inbound turn doesn't wait
                        // for this loop's threshold to elapse.
                        await TryRestartAsync(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check failed for agent {AgentId}", entry.AgentId);
                var failures = entry.ConsecutiveFailures + 1;

                _entries[entry.AgentId] = entry with
                {
                    HealthStatus = AgentHealthStatus.Unhealthy,
                    ConsecutiveFailures = failures,
                };

                if (failures >= UnhealthyThreshold)
                {
                    await TryRestartAsync(entry);
                }
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
    /// Attempts to restart an unhealthy agent by stopping the old container
    /// and starting a fresh one.
    /// </summary>
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

    private async Task TryRestartAsync(PersistentAgentEntry entry)
    {
        _logger.LogInformation(
            EventIds.AgentRestarting,
            "Attempting restart of persistent agent {AgentId}", entry.AgentId);

        try
        {
            if (entry.Definition?.Execution?.Image is null)
            {
                _logger.LogWarning(
                    "Cannot restart agent {AgentId}: no definition/image available; keeping as unavailable.",
                    entry.AgentId);
                // Keep the entry in the registry so the portal chip stays
                // "unavailable" rather than flipping back to "idle". Reset
                // ConsecutiveFailures so we don't re-enter TryRestartAsync on
                // every subsequent health tick — the cycle naturally rebuilds
                // to the restart threshold over UnhealthyThreshold ticks.
                _entries[entry.AgentId] = entry with { ConsecutiveFailures = 0 };
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var lifecycle = scope.ServiceProvider.GetRequiredService<PersistentAgentLifecycle>();
            await lifecycle.DeployAsync(entry.AgentId, cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart persistent agent {AgentId}", entry.AgentId);
            _entries.TryRemove(entry.AgentId, out _);
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
}

/// <summary>
/// Health status of a persistent agent service.
/// </summary>
public enum AgentHealthStatus
{
    /// <summary>The agent is responding to health probes.</summary>
    Healthy,

    /// <summary>The agent has failed consecutive health probes and needs restart.</summary>
    Unhealthy
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
/// <param name="Definition">The agent definition, retained for restart.</param>
public record PersistentAgentEntry(
    string AgentId,
    Uri Endpoint,
    string? ContainerId,
    DateTimeOffset StartedAt,
    AgentHealthStatus HealthStatus = AgentHealthStatus.Healthy,
    int ConsecutiveFailures = 0,
    AgentDefinition? Definition = null,
    string? SidecarId = null,
    string? SidecarNetworkName = null);
