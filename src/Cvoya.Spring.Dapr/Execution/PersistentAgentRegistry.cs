// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Execution;

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
    ILoggerFactory loggerFactory) : IHostedService, IDisposable
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PersistentAgentRegistry>();
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
    public void Register(string agentId, Uri endpoint, string? containerId, AgentDefinition? definition = null)
    {
        var entry = new PersistentAgentEntry(
            agentId, endpoint, containerId, DateTimeOffset.UtcNow,
            HealthStatus: AgentHealthStatus.Healthy,
            ConsecutiveFailures: 0,
            Definition: definition);
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
    /// Returns a snapshot of all registered entries. Used for testing and diagnostics.
    /// </summary>
    internal IReadOnlyCollection<PersistentAgentEntry> GetAllEntries()
    {
        return _entries.Values.ToList().AsReadOnly();
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
            .Where(e => e.ContainerId is not null)
            .Select(e => StopContainerSafeAsync(e.ContainerId!, cancellationToken));

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
                var healthy = await ProbeHealthAsync(entry.Endpoint);

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
    internal async Task<bool> ProbeHealthAsync(Uri endpoint)
    {
        using var httpClient = httpClientFactory.CreateClient("PersistentAgentHealthCheck");
        httpClient.Timeout = HealthProbeTimeout;

        var agentCardUri = new Uri(endpoint, ".well-known/agent.json");

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
    private async Task TryRestartAsync(PersistentAgentEntry entry)
    {
        _logger.LogInformation(
            EventIds.AgentRestarting,
            "Attempting restart of persistent agent {AgentId}", entry.AgentId);

        try
        {
            // Stop the old container.
            if (entry.ContainerId is not null)
            {
                await StopContainerSafeAsync(entry.ContainerId, CancellationToken.None);
            }

            // We need the definition to restart. If not available, just remove.
            if (entry.Definition?.Execution?.Image is null)
            {
                _logger.LogWarning(
                    "Cannot restart agent {AgentId}: no definition/image available. Removing from registry.",
                    entry.AgentId);
                _entries.TryRemove(entry.AgentId, out _);
                return;
            }

            // Start a fresh container.
            var config = new ContainerConfig(
                Image: entry.Definition.Execution.Image,
                ExtraHosts: ["host.docker.internal:host-gateway"]);

            var newContainerId = await containerRuntime.StartAsync(config, CancellationToken.None);

            // Wait for the new container to become ready.
            var ready = await WaitForA2AReadyAsync(
                entry.Endpoint, A2AExecutionDispatcher.ReadinessTimeout, CancellationToken.None);

            if (ready)
            {
                _entries[entry.AgentId] = entry with
                {
                    ContainerId = newContainerId,
                    HealthStatus = AgentHealthStatus.Healthy,
                    ConsecutiveFailures = 0,
                    StartedAt = DateTimeOffset.UtcNow
                };

                _logger.LogInformation(
                    EventIds.AgentRestarted,
                    "Persistent agent {AgentId} restarted successfully (new container {ContainerId})",
                    entry.AgentId, newContainerId);
            }
            else
            {
                _logger.LogError(
                    "Restart of agent {AgentId} failed: A2A endpoint did not become ready. Removing from registry.",
                    entry.AgentId);
                await StopContainerSafeAsync(newContainerId, CancellationToken.None);
                _entries.TryRemove(entry.AgentId, out _);
            }
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
    internal async Task<bool> WaitForA2AReadyAsync(Uri endpoint, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            if (await ProbeHealthAsync(endpoint))
            {
                return true;
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
    AgentDefinition? Definition = null);