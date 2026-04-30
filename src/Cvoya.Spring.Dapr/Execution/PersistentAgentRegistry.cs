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
    private Timer? _healthTimer;

    /// <summary>
    /// Default interval between health-check sweeps.
    /// </summary>
    internal static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Number of consecutive health-check failures before an agent is marked unhealthy.
    /// </summary>
    internal const int UnhealthyThreshold = 3;

    /// <summary>
    /// Timeout for a single health-probe HTTP request.
    /// </summary>
    internal static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(5);

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
    /// Marks an agent as unhealthy so it will be restarted on the next health sweep.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    public void MarkUnhealthy(string agentId)
    {
        if (_entries.TryGetValue(agentId, out var entry))
        {
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

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(EventIds.HealthMonitorStarting, "Persistent agent health monitor starting");
        _healthTimer = new Timer(
            callback: _ => _ = RunHealthChecksAsync(),
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
            try
            {
                var healthy = await ProbeHealthAsync(entry);

                if (healthy)
                {
                    // Reset failure count on success.
                    if (entry.ConsecutiveFailures > 0)
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
                    if (failures >= UnhealthyThreshold)
                    {
                        _logger.LogWarning(
                            EventIds.AgentUnhealthy,
                            "Agent {AgentId} marked unhealthy after {Failures} consecutive failures",
                            entry.AgentId, failures);

                        _entries[entry.AgentId] = entry with
                        {
                            HealthStatus = AgentHealthStatus.Unhealthy,
                            ConsecutiveFailures = failures
                        };

                        // Attempt restart.
                        await TryRestartAsync(entry);
                    }
                    else
                    {
                        _entries[entry.AgentId] = entry with { ConsecutiveFailures = failures };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check failed for agent {AgentId}", entry.AgentId);
                var failures = entry.ConsecutiveFailures + 1;
                var status = failures >= UnhealthyThreshold
                    ? AgentHealthStatus.Unhealthy
                    : entry.HealthStatus;

                _entries[entry.AgentId] = entry with
                {
                    HealthStatus = status,
                    ConsecutiveFailures = failures
                };

                if (status == AgentHealthStatus.Unhealthy)
                {
                    await TryRestartAsync(entry);
                }
            }
        }
    }

    /// <summary>
    /// Probes the A2A Agent Card endpoint to verify the agent is healthy.
    /// </summary>
    /// <remarks>
    /// When the entry carries a container id, the probe is dispatched from
    /// the host process via
    /// <see cref="IContainerRuntime.ProbeHttpFromHostAsync"/> so it works
    /// regardless of network topology and does not require any binary
    /// (<c>wget</c>, <c>curl</c>) inside the workload image (issue #1175).
    /// Entries without a container id (legacy externally-registered persistent
    /// agents) fall back to the direct HTTP probe.
    /// </remarks>
    internal async Task<bool> ProbeHealthAsync(PersistentAgentEntry entry)
    {
        var agentCardUri = new Uri(entry.Endpoint, ".well-known/agent.json").ToString();

        if (!string.IsNullOrEmpty(entry.ContainerId))
        {
            using var probeCts = new CancellationTokenSource(HealthProbeTimeout);
            try
            {
                return await containerRuntime.ProbeHttpFromHostAsync(
                    entry.ContainerId, agentCardUri, probeCts.Token);
            }
            catch (Exception)
            {
                return false;
            }
        }

        using var httpClient = httpClientFactory.CreateClient("PersistentAgentHealthCheck");
        httpClient.Timeout = HealthProbeTimeout;

        try
        {
            var response = await httpClient.GetAsync(agentCardUri);
            return response.IsSuccessStatusCode;
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
                    "Cannot restart agent {AgentId}: no definition/image available. Removing from registry.",
                    entry.AgentId);
                _entries.TryRemove(entry.AgentId, out _);
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
    /// The probe is dispatched from the host process via
    /// <see cref="IContainerRuntime.ProbeHttpFromHostAsync"/> so it works
    /// even when the worker process and the agent container live on different
    /// networks, and does not require any binary (<c>wget</c>) inside the
    /// workload image (issue #1175).
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
                if (await containerRuntime.ProbeHttpFromHostAsync(containerId, agentCardUri, cts.Token))
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