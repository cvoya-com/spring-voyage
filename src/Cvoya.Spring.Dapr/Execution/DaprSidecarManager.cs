// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Manages Dapr sidecar containers by routing every container operation
/// through <see cref="IContainerRuntime"/> — i.e. through the host
/// <c>spring-dispatcher</c> service. The worker process holds zero
/// <c>Process.Start</c> calls of its own (Stage 2 of #522 / #1063).
/// </summary>
/// <remarks>
/// <para>
/// Health polling uses
/// <see cref="IContainerRuntime.ProbeContainerHttpAsync"/> to ask the
/// dispatcher to run a one-shot <c>wget --spider</c> against the sidecar's
/// loopback healthz URL. The sidecar itself runs on a private per-app
/// network the worker does not share, so a direct
/// <c>HttpClient</c>-from-worker probe would not reach it; routing through
/// the dispatcher's container namespace is the shortest path that keeps
/// the worker free of a podman/docker binding.
/// </para>
/// <para>
/// Image and timeout knobs that used to live as <c>const</c>s in this file
/// (and inside <see cref="ContainerRuntimeOptions"/>) now bind from
/// <see cref="DaprSidecarOptions"/> so operators can pin a daprd version
/// without recompiling.
/// </para>
/// </remarks>
public class DaprSidecarManager(
    IContainerRuntime runtime,
    IOptions<DaprSidecarOptions> options,
    ILoggerFactory loggerFactory) : IDaprSidecarManager
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DaprSidecarManager>();
    private readonly DaprSidecarOptions _options = options.Value;

    /// <summary>
    /// HTTP port the daprd healthz endpoint binds inside the sidecar
    /// container. Hardcoded because <see cref="WaitForHealthyAsync"/>
    /// only receives a sidecar id (the contract on
    /// <see cref="IDaprSidecarManager"/> doesn't carry the configured
    /// port through). Every <see cref="StartSidecarAsync"/> caller in
    /// the platform passes <see cref="DaprSidecarConfig.DaprHttpPort"/>
    /// = 3500, so this matches the previous hardcoded value
    /// (the pre-Stage-2 implementation also hardcoded 3500). If a
    /// caller ever needs a non-default port, the right fix is to widen
    /// the interface — not to plumb the value through state.
    /// </summary>
    private const int DaprHealthHttpPort = 3500;

    /// <inheritdoc />
    public async Task<DaprSidecarInfo> StartSidecarAsync(DaprSidecarConfig config, CancellationToken ct = default)
    {
        var sidecarName = $"spring-dapr-{config.AppId}-{Guid.NewGuid():N}"[..48];
        var containerConfig = BuildSidecarContainerConfig(config, sidecarName);

        _logger.LogInformation(
            EventIds.SidecarStarting,
            "Starting Dapr sidecar {SidecarName} for app {AppId} on ports HTTP={HttpPort} gRPC={GrpcPort}",
            sidecarName, config.AppId, config.DaprHttpPort, config.DaprGrpcPort);

        try
        {
            var containerId = await runtime.StartAsync(containerConfig, ct);

            _logger.LogInformation(
                EventIds.SidecarStarted,
                "Dapr sidecar {SidecarName} started with container ID {ContainerId}",
                sidecarName, containerId);

            // We return the human-readable name (matches the previous
            // contract) — the runtime resolves either by id or by name on
            // subsequent calls, and using the name keeps logs symmetric
            // with the labels we set on the container.
            return new DaprSidecarInfo(sidecarName, config.DaprHttpPort, config.DaprGrpcPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                EventIds.SidecarStartFailed, ex,
                "Failed to start Dapr sidecar {SidecarName}", sidecarName);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopSidecarAsync(string sidecarId, CancellationToken ct = default)
    {
        _logger.LogInformation(
            EventIds.SidecarStopping,
            "Stopping Dapr sidecar {SidecarId}", sidecarId);

        try
        {
            // The runtime's StopAsync semantics are "stop AND remove"
            // (see ProcessContainerRuntime.StopAsync), matching the
            // previous two-step podman stop / podman rm sequence.
            await runtime.StopAsync(sidecarId, ct);
        }
        catch (Exception ex)
        {
            // Best-effort teardown: log and move on. The lifecycle manager's
            // teardown path also calls this and tolerates failures.
            _logger.LogWarning(ex, "Failed to stop Dapr sidecar {SidecarId}", sidecarId);
        }
    }

    /// <inheritdoc />
    public async Task<bool> WaitForHealthyAsync(string sidecarId, TimeSpan timeout, CancellationToken ct = default)
    {
        // Caller-provided timeout wins over the configured default so call
        // sites that already pass a context-aware deadline (lifecycle
        // manager's DefaultHealthTimeout) keep their existing behavior.
        var effectiveTimeout = timeout > TimeSpan.Zero ? timeout : _options.HealthTimeout;
        var pollInterval = _options.HealthPollInterval;

        _logger.LogInformation(
            EventIds.SidecarHealthCheck,
            "Waiting for Dapr sidecar {SidecarId} to become healthy (timeout: {Timeout})",
            sidecarId, effectiveTimeout);

        // Loopback URL: we run the probe inside the sidecar container's own
        // network namespace (the dispatcher's exec routes it there), so
        // localhost resolves to daprd's bound interface regardless of which
        // network the sidecar is attached to in the worker / app graph.
        var healthUrl = $"http://localhost:{DaprHealthHttpPort}/v1.0/healthz";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            try
            {
                var healthy = await runtime.ProbeContainerHttpAsync(sidecarId, healthUrl, timeoutCts.Token);
                if (healthy)
                {
                    _logger.LogInformation(
                        EventIds.SidecarHealthy,
                        "Dapr sidecar {SidecarId} is healthy", sidecarId);
                    return true;
                }
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Local timeout — fall through to the warning below.
                break;
            }

            try
            {
                await Task.Delay(pollInterval, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogWarning(
            EventIds.SidecarUnhealthy,
            "Dapr sidecar {SidecarId} did not become healthy within {Timeout}", sidecarId, effectiveTimeout);
        return false;
    }

    /// <summary>
    /// Translates a <see cref="DaprSidecarConfig"/> + container name into
    /// the runtime-level <see cref="ContainerConfig"/> the dispatcher
    /// expects. Public for tests; the previous string-arg builder
    /// (<c>BuildSidecarRunArguments</c>) was test-only too.
    /// </summary>
    internal ContainerConfig BuildSidecarContainerConfig(DaprSidecarConfig config, string sidecarName)
    {
        var labels = new Dictionary<string, string>
        {
            ["spring.managed"] = "true",
            ["spring.role"] = "dapr-sidecar",
            ["spring.app-id"] = config.AppId,
        };

        var mounts = new List<string>();
        if (config.ComponentsPath is not null)
        {
            mounts.Add($"{config.ComponentsPath}:/components");
        }

        // Build the daprd command line. Mirrors the original
        // BuildSidecarRunArguments verbatim except we now pass it as the
        // container's command argument rather than splicing it into a
        // long `podman run` string. The dispatcher's runtime appends it
        // after the image, matching the previous behavior.
        var commandParts = new List<string>
        {
            "./daprd",
            $"--app-id {config.AppId}",
            $"--app-port {config.AppPort}",
            $"--dapr-http-port {config.DaprHttpPort}",
            $"--dapr-grpc-port {config.DaprGrpcPort}",
        };

        if (config.ComponentsPath is not null)
        {
            commandParts.Add("--resources-path /components");
        }

        return new ContainerConfig(
            Image: _options.Image,
            Command: string.Join(' ', commandParts),
            VolumeMounts: mounts.Count > 0 ? mounts : null,
            NetworkName: config.NetworkName,
            Labels: labels);
    }

    /// <summary>
    /// Event IDs for Dapr sidecar management logging (range 2200-2299).
    /// </summary>
    private static class EventIds
    {
        public static readonly EventId SidecarStarting = new(2210, nameof(SidecarStarting));
        public static readonly EventId SidecarStarted = new(2211, nameof(SidecarStarted));
        public static readonly EventId SidecarStartFailed = new(2212, nameof(SidecarStartFailed));
        public static readonly EventId SidecarStopping = new(2213, nameof(SidecarStopping));
        public static readonly EventId SidecarHealthCheck = new(2214, nameof(SidecarHealthCheck));
        public static readonly EventId SidecarHealthy = new(2215, nameof(SidecarHealthy));
        public static readonly EventId SidecarUnhealthy = new(2216, nameof(SidecarUnhealthy));
    }
}