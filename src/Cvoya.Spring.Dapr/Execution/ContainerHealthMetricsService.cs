// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Diagnostics.Metrics;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Background service that periodically polls the native container
/// HEALTHCHECK status for every tracked detached agent container and
/// emits an OpenTelemetry gauge (<c>spring.container.healthy</c>)
/// via the BCL <see cref="System.Diagnostics.Metrics.Meter"/> API.
///
/// <para>
/// This closes issue #1378: operators can graph <c>spring.container.healthy</c>
/// in any OpenTelemetry-compatible backend (Prometheus, Grafana, Datadog, …)
/// without polling the pull-based <c>GET /v1/containers/{id}/health</c>
/// dispatcher endpoint.
/// </para>
///
/// <para>
/// <b>How it interacts with <see cref="PersistentAgentRegistry"/>.</b>
/// The registry runs an HTTP-probe health loop that restarts containers
/// whose A2A Agent Card endpoint stops responding. This service is
/// complementary: it reads the native HEALTHCHECK result (via
/// <see cref="IContainerRuntime.GetHealthAsync"/>) and projects it as a
/// time-series metric. The two signals are independent — an agent can
/// pass the HTTP probe and fail the HEALTHCHECK (or vice-versa); operators
/// can alert on either.
/// </para>
///
/// <para>
/// <b>Metric names and tag set (operators: graph these).</b> See
/// <see cref="MetricNames"/> and <see cref="Tags"/> for the full list.
/// </para>
/// </summary>
public sealed class ContainerHealthMetricsService : IHostedService, IDisposable
{
    /// <summary>
    /// Metric names emitted by this service. Stable; follow the
    /// <c>spring.*</c> namespace convention used across the platform.
    /// </summary>
    public static class MetricNames
    {
        /// <summary>
        /// Gauge: 1 when the container's native HEALTHCHECK reports healthy
        /// (or when no HEALTHCHECK is declared — healthy by convention),
        /// 0 otherwise.  Tagged with <see cref="Tags.AgentId"/> and
        /// <see cref="Tags.ContainerId"/>.
        /// </summary>
        public const string ContainerHealthy = "spring.container.healthy";
    }

    /// <summary>
    /// Tag keys attached to every metric observation.
    /// </summary>
    public static class Tags
    {
        /// <summary>Agent identifier from <see cref="PersistentAgentEntry.AgentId"/>.</summary>
        public const string AgentId = "agent_id";

        /// <summary>Container identifier from <see cref="PersistentAgentEntry.ContainerId"/>.</summary>
        public const string ContainerId = "container_id";
    }

    /// <summary>
    /// OpenTelemetry / BCL Meter name. Use this to filter the meter in
    /// your OTel SDK configuration.
    /// </summary>
    public const string MeterName = "Cvoya.Spring.Dapr";

    /// <summary>Default interval between health-gauge sweeps.</summary>
    internal static readonly TimeSpan HealthGaugeInterval = TimeSpan.FromSeconds(30);

    private readonly PersistentAgentRegistry _registry;
    private readonly IContainerRuntime _containerRuntime;
    private readonly ILogger _logger;
    private readonly Meter _meter;
    private readonly ObservableGauge<int> _healthGauge;
    private Timer? _timer;

    public ContainerHealthMetricsService(
        PersistentAgentRegistry registry,
        IContainerRuntime containerRuntime,
        ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _containerRuntime = containerRuntime;
        _logger = loggerFactory.CreateLogger<ContainerHealthMetricsService>();

        // BCL Meter — picked up automatically by any OpenTelemetry SDK that
        // listens to meters with name "Cvoya.Spring.Dapr" (no extra NuGet
        // required in the Dapr project; the SDK is wired in the host project).
        _meter = new Meter(MeterName, version: "1.0");

        // We use a pull-based observable gauge: the SDK calls the callback
        // on each collection cycle and we return the latest snapshot. Doing
        // the actual GetHealthAsync call in the callback would block, so
        // instead we keep a periodic background timer that refreshes
        // _latestHealthValues and the gauge callback just reads them.
        _healthGauge = _meter.CreateObservableGauge(
            name: MetricNames.ContainerHealthy,
            observeValues: ObserveHealthValues,
            description: "1 when the container HEALTHCHECK is healthy (or no HEALTHCHECK declared), 0 otherwise.");
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            EventIds.HealthMetricsStarting,
            "Container health metrics service starting (interval {Interval}s)",
            HealthGaugeInterval.TotalSeconds);

        _timer = new Timer(
            callback: _ => _ = RefreshHealthSnapshotAsync(),
            state: null,
            dueTime: TimeSpan.Zero,
            period: HealthGaugeInterval);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(EventIds.HealthMetricsStopping, "Container health metrics service stopping");

        // Use Interlocked.Exchange so that a concurrent Dispose() call (which can
        // occur on abnormal host-shutdown paths before StartAsync completes) cannot
        // leave _timer as a non-null reference to an already-disposed Timer that
        // then causes a NullReferenceException inside DisposeAsync().
        var timer = Interlocked.Exchange(ref _timer, null);
        if (timer is not null)
        {
            await timer.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Null out _timer after disposing so that a subsequent StopAsync call
        // (which can race with Dispose in abnormal host-shutdown paths) skips the
        // DisposeAsync branch rather than calling it on an already-disposed timer.
        var timer = Interlocked.Exchange(ref _timer, null);
        timer?.Dispose();
        _meter.Dispose();
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    // Latest health snapshot: agentId -> (containerId, healthy).
    // Updated on the timer thread; read by ObserveHealthValues on the
    // OTel collection thread.  Volatile read of the reference is safe
    // because we replace the whole dictionary atomically.
    private volatile IReadOnlyDictionary<string, (string ContainerId, int Healthy)> _snapshot =
        new Dictionary<string, (string, int)>();

    private IEnumerable<Measurement<int>> ObserveHealthValues()
    {
        var snapshot = _snapshot;
        foreach (var (agentId, (containerId, healthy)) in snapshot)
        {
            yield return new Measurement<int>(
                healthy,
                new KeyValuePair<string, object?>(Tags.AgentId, agentId),
                new KeyValuePair<string, object?>(Tags.ContainerId, containerId));
        }
    }

    /// <summary>
    /// Queries <see cref="IContainerRuntime.GetHealthAsync"/> for every
    /// registered persistent agent that has a container id and builds a
    /// fresh <see cref="_snapshot"/>. Runs on the background timer.
    /// </summary>
    private async Task RefreshHealthSnapshotAsync()
    {
        var entries = _registry.GetAllEntries()
            .Where(e => !string.IsNullOrEmpty(e.ContainerId))
            .ToList();

        if (entries.Count == 0)
        {
            _snapshot = new Dictionary<string, (string, int)>();
            return;
        }

        var next = new Dictionary<string, (string ContainerId, int Healthy)>(entries.Count);

        foreach (var entry in entries)
        {
            var containerId = entry.ContainerId!;
            try
            {
                var health = await _containerRuntime.GetHealthAsync(containerId, CancellationToken.None);
                var healthy = health.Healthy ? 1 : 0;

                next[entry.AgentId] = (containerId, healthy);

                _logger.LogDebug(
                    EventIds.HealthMetricObserved,
                    "Container health: agent={AgentId} container={ContainerId} healthy={Healthy} detail={Detail}",
                    entry.AgentId, containerId, healthy, health.Detail);
            }
            catch (Exception ex)
            {
                // GetHealthAsync throws InvalidOperationException when the
                // container id is unknown (container was reaped between the
                // registry snapshot and the probe). Treat as unhealthy; the
                // registry's own health loop will handle restart.
                _logger.LogDebug(
                    EventIds.HealthMetricProbeFailed,
                    ex,
                    "GetHealthAsync failed for agent {AgentId} container {ContainerId}; emitting unhealthy",
                    entry.AgentId, containerId);

                next[entry.AgentId] = (containerId, 0);
            }
        }

        _snapshot = next;
    }

    /// <summary>
    /// Event IDs for container health metrics service logging (range 2260-2279
    /// per CONVENTIONS.md § 9, Cvoya.Spring.Dapr.Execution).
    /// </summary>
    private static class EventIds
    {
        public static readonly EventId HealthMetricsStarting = new(2260, nameof(HealthMetricsStarting));
        public static readonly EventId HealthMetricsStopping = new(2261, nameof(HealthMetricsStopping));
        public static readonly EventId HealthMetricObserved = new(2262, nameof(HealthMetricObserved));
        public static readonly EventId HealthMetricProbeFailed = new(2263, nameof(HealthMetricProbeFailed));
    }
}