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
/// <see cref="IContainerRuntime.ProbeHttpFromTransientContainerAsync"/> — the
/// dispatcher spawns a throwaway curl container on the sidecar's bridge
/// network and asks <c>/v1.0/healthz/outbound</c>. The per-container
/// <c>podman exec wget</c> probe used elsewhere is unusable for daprd
/// because the upstream <c>daprio/daprd</c> image is effectively distroless
/// (no shell, no wget, no curl). The sidecar itself runs on a private
/// per-app network the worker does not share, so a direct
/// <c>HttpClient</c>-from-worker probe would not reach it; routing through
/// the dispatcher keeps the worker free of a podman/docker binding.
/// </para>
/// <para>
/// Image and timeout knobs that used to live as <c>const</c>s in this file
/// (and inside <see cref="ContainerRuntimeOptions"/>) now bind from
/// <see cref="DaprSidecarOptions"/> so operators can pin a daprd version
/// without recompiling. The probe-container image is also configurable via
/// <see cref="DaprSidecarOptions.CurlProbeImage"/> for air-gapped sites.
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
    /// container. Hardcoded because every <see cref="StartSidecarAsync"/>
    /// caller in the platform passes
    /// <see cref="DaprSidecarConfig.DaprHttpPort"/> = 3500, matching the
    /// previous hardcoded value (pre-Stage-2 implementation also hardcoded
    /// 3500). If a caller ever needs a non-default port, plumb it through
    /// <see cref="DaprSidecarInfo"/> rather than reintroducing state.
    /// </summary>
    private const int DaprHealthHttpPort = 3500;

    /// <inheritdoc />
    public async Task<DaprSidecarInfo> StartSidecarAsync(DaprSidecarConfig config, CancellationToken ct = default)
    {
        // Probe path needs a network for DNS resolution; require it up front
        // so callers see a clean validation error rather than a healthz timeout.
        if (string.IsNullOrEmpty(config.NetworkName))
        {
            throw new InvalidOperationException(
                $"DaprSidecarConfig.NetworkName is required (app {config.AppId}) — health probes resolve the sidecar by name on this network.");
        }

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

            // SidecarId is the dispatcher-assigned container name (today
            // ProcessContainerRuntime.StartAsync overrides --name with its
            // own spring-persistent-<guid>); the picked sidecarName above
            // only flows into labels. Returning containerId is what makes
            // WaitForHealthyAsync's transient curl probe reach the daprd
            // by DNS on NetworkName — only the dispatcher-assigned name is
            // registered with the bridge.
            return new DaprSidecarInfo(
                containerId, config.DaprHttpPort, config.DaprGrpcPort, config.NetworkName);
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
    public async Task<bool> WaitForHealthyAsync(DaprSidecarInfo sidecar, TimeSpan timeout, CancellationToken ct = default)
    {
        // Caller-provided timeout wins over the configured default so call
        // sites that already pass a context-aware deadline (lifecycle
        // manager's DefaultHealthTimeout) keep their existing behavior.
        var effectiveTimeout = timeout > TimeSpan.Zero ? timeout : _options.HealthTimeout;
        var pollInterval = _options.HealthPollInterval;

        _logger.LogInformation(
            EventIds.SidecarHealthCheck,
            "Waiting for Dapr sidecar {SidecarId} to become healthy (timeout: {Timeout})",
            sidecar.SidecarId, effectiveTimeout);

        // /v1.0/healthz/outbound (not /v1.0/healthz) so daprd reports ready
        // as soon as components + control plane are reachable, without also
        // waiting for the paired app container — which we only start AFTER
        // this method returns true (chicken-and-egg otherwise). Mirrors
        // deployment/deploy.sh's wait_sidecar_ready helper.
        //
        // The probe runs in a throwaway curl container on the sidecar's
        // bridge network; the upstream daprio/daprd image is distroless so
        // the per-container `podman exec wget` probe used elsewhere is
        // unusable here. See ProbeHttpFromTransientContainerAsync.
        var healthUrl = $"http://{sidecar.SidecarId}:{DaprHealthHttpPort}/v1.0/healthz/outbound";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            try
            {
                var healthy = await runtime.ProbeHttpFromTransientContainerAsync(
                    _options.CurlProbeImage, sidecar.NetworkName, healthUrl, timeoutCts.Token);
                if (healthy)
                {
                    _logger.LogInformation(
                        EventIds.SidecarHealthy,
                        "Dapr sidecar {SidecarId} is healthy", sidecar.SidecarId);
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
            "Dapr sidecar {SidecarId} did not become healthy within {Timeout}", sidecar.SidecarId, effectiveTimeout);
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

        // Build the daprd argv. Each token is one argv entry — the dispatcher
        // forwards it through ProcessStartInfo.ArgumentList, no shell splitting
        // and no whitespace fragility (cf. the bug fixed in #1063 / #1093).
        var commandParts = new List<string>
        {
            "./daprd",
            "--app-id", config.AppId,
            "--app-port", config.AppPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--dapr-http-port", config.DaprHttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--dapr-grpc-port", config.DaprGrpcPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (config.ComponentsPath is not null)
        {
            commandParts.Add("--resources-path");
            commandParts.Add("/components");
        }

        if (config.PlacementHostAddress is { Length: > 0 } ph)
        {
            commandParts.Add("--placement-host-address");
            commandParts.Add(ph);
        }

        if (config.SchedulerHostAddress is { Length: > 0 } sh)
        {
            commandParts.Add("--scheduler-host-address");
            commandParts.Add(sh);
        }

        if (config.DaprConfigFilePath is { Length: > 0 } dcfp)
        {
            mounts.Add($"{dcfp}:/config/config.yaml:ro");
            commandParts.Add("--config");
            commandParts.Add("/config/config.yaml");
        }

        return new ContainerConfig(
            Image: _options.Image,
            Command: commandParts,
            VolumeMounts: mounts.Count > 0 ? mounts : null,
            NetworkName: config.NetworkName,
            AdditionalNetworks: config.AdditionalNetworks,
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