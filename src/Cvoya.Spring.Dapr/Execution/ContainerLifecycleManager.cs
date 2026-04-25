// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Composes <see cref="IContainerRuntime"/> and <see cref="IDaprSidecarManager"/> to manage
/// the full lifecycle of an application container with its Dapr sidecar.
/// Stage 2 of #522 / #1063 removed the worker-side <c>Process.Start</c>
/// calls this class held for network create/remove; both now route through
/// <see cref="IContainerRuntime.CreateNetworkAsync"/> and
/// <see cref="IContainerRuntime.RemoveNetworkAsync"/> on the dispatcher.
/// </summary>
public class ContainerLifecycleManager(
    IContainerRuntime containerRuntime,
    IDaprSidecarManager sidecarManager,
    IOptions<DaprSidecarOptions> sidecarOptions,
    ILoggerFactory loggerFactory)
{
    private static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = loggerFactory.CreateLogger<ContainerLifecycleManager>();
    private readonly DaprSidecarOptions _sidecarOptions = sidecarOptions.Value;

    /// <summary>
    /// Launches an application container with a Dapr sidecar on a shared network.
    /// Creates the network, starts the sidecar, waits for it to become healthy,
    /// then starts the app container.
    /// </summary>
    /// <param name="config">The container configuration. Must have <see cref="ContainerConfig.DaprEnabled"/> set to true.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The result of the application container execution, along with sidecar and network info.</returns>
    public async Task<ContainerLifecycleResult> LaunchWithSidecarAsync(ContainerConfig config, CancellationToken ct = default)
    {
        var networkName = config.NetworkName ?? $"spring-net-{Guid.NewGuid():N}"[..32];
        var appId = config.DaprAppId ?? $"spring-app-{Guid.NewGuid():N}"[..32];
        var appPort = config.DaprAppPort ?? 8080;
        var daprHttpPort = 3500;
        var daprGrpcPort = 50001;

        // Tenant network the workflow / unit container is dual-attached to in
        // addition to the per-app spring-net-<guid> bridge — that bridge keeps
        // app↔sidecar traffic isolated; the tenant attach is what lets the
        // container reach tenant infrastructure (Ollama, peer agents, future
        // tenant services) by uniform DNS. ADR 0028 — Decision A; closes the
        // workflow-side slice in issue #1166.
        //
        // The daprd sidecar is also second-attached to the tenant network when
        // the per-app primary network differs from it, so the sidecar can still
        // resolve the placement and scheduler services after deploy.sh
        // dual-attaches the control plane to the tenant bridge (V2 single-tenant
        // interim topology; see ADR 0028 status note).
        var tenantNetwork = ContainerConfigBuilder.TenantNetworkName;

        _logger.LogInformation(
            EventIds.LifecycleStarting,
            "Starting container lifecycle for app {AppId} on network {NetworkName} (dual-attached to tenant network {TenantNetwork})",
            appId, networkName, tenantNetwork);

        // Step 1: Create the per-workflow bridge (idempotent on re-create).
        await CreateNetworkAsync(networkName, ct);

        // Step 1b: Ensure the tenant bridge exists. Idempotent on the
        // dispatcher side, so a second create with the same name is a 200.
        // OSS deploys this once via deploy.sh, but we don't rely on that —
        // a fresh clone / partial bring-up should still launch successfully.
        await CreateNetworkAsync(tenantNetwork, ct);

        DaprSidecarInfo? sidecarInfo = null;
        try
        {
            // Step 2: Start the Dapr sidecar.
            var sidecarConfig = BuildDaprSidecarConfig(
                appId, appPort, daprHttpPort, daprGrpcPort, networkName, tenantNetwork, config);

            sidecarInfo = await sidecarManager.StartSidecarAsync(sidecarConfig, ct);

            // Step 3: Wait for sidecar health.
            var healthy = await sidecarManager.WaitForHealthyAsync(
                sidecarInfo, DefaultHealthTimeout, ct);

            if (!healthy)
            {
                throw new InvalidOperationException(
                    $"Dapr sidecar {sidecarInfo.SidecarId} did not become healthy within {DefaultHealthTimeout}.");
            }

            // Step 4: Augment the app config with Dapr env vars and network.
            // Sibling daprd is not on 127.0.0.1 — only DAPR_HTTP_PORT/GRPC_PORT
            // makes Dapr + durabletask clients default to loopback and fail
            // (see deploy.sh: DAPR_HTTP_ENDPOINT / DAPR_GRPC_ENDPOINT per app).
            var augmentedEnv = new Dictionary<string, string>(
                config.EnvironmentVariables ?? new Dictionary<string, string>());
            AddDaprSidecarClientEnv(augmentedEnv, sidecarInfo);

            var augmentedConfig = config with
            {
                NetworkName = networkName,
                AdditionalNetworks = MergeAdditionalNetworks(config.AdditionalNetworks, tenantNetwork),
                EnvironmentVariables = augmentedEnv
            };

            // Step 5: Run the application container.
            var result = await containerRuntime.RunAsync(augmentedConfig, ct);

            _logger.LogInformation(
                EventIds.LifecycleCompleted,
                "Container lifecycle completed for app {AppId}. Exit code: {ExitCode}",
                appId, result.ExitCode);

            return new ContainerLifecycleResult(result, sidecarInfo, networkName);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                EventIds.LifecycleFailed, ex,
                "Container lifecycle failed for app {AppId}. Cleaning up.", appId);

            // Best-effort teardown on failure.
            await TeardownAsync(null, sidecarInfo?.SidecarId, networkName, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Launches a detached (long-running) app container with a Dapr sidecar
    /// on a per-launch <c>spring-net-&lt;guid&gt;</c> bridge, dual-attached to
    /// the tenant network — same as <see cref="LaunchWithSidecarAsync"/>, but
    /// uses <see cref="IContainerRuntime.StartAsync"/> so the A2A server and
    /// the <c>dapr-agents</c> workflow runtime keep running. Used for
    /// <c>tool=dapr-agent</c> ephemeral and persistent dispatches.
    /// </summary>
    public async Task<DetachedContainerLifecycleResult> LaunchWithSidecarDetachedAsync(
        ContainerConfig config, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config.DaprAppId);
        if (config.DaprAppPort is null)
        {
            throw new InvalidOperationException(
                "LaunchWithSidecarDetachedAsync requires DaprAppPort to be set on the container config.");
        }

        var networkName = config.NetworkName ?? $"spring-net-{Guid.NewGuid():N}"[..32];
        var appId = config.DaprAppId;
        var appPort = config.DaprAppPort.Value;
        const int daprHttpPort = 3500;
        const int daprGrpcPort = 50001;
        var tenantNetwork = ContainerConfigBuilder.TenantNetworkName;

        _logger.LogInformation(
            EventIds.LifecycleStarting,
            "Starting detached sidecar lifecycle for app {AppId} on network {NetworkName} (tenant {TenantNetwork})",
            appId, networkName, tenantNetwork);

        await CreateNetworkAsync(networkName, ct);
        await CreateNetworkAsync(tenantNetwork, ct);

        // Pre-allocate the agent container's name so the daprd sidecar
        // can be started knowing which DNS name to reach the app on
        // (`--app-channel-address`). Without a stable, pre-known name
        // daprd would default to `127.0.0.1` which is daprd's own
        // loopback — wrong for separate-container app/sidecar layouts.
        // The runtime honours config.ContainerName when set, otherwise
        // it'd assign its own random name and we'd have nothing to hand
        // to daprd up front.
        var agentContainerName = config.ContainerName
            ?? $"spring-persistent-{Guid.NewGuid():N}";

        DaprSidecarInfo? sidecarInfo = null;
        try
        {
            var sidecarConfig = BuildDaprSidecarConfig(
                appId, appPort, daprHttpPort, daprGrpcPort, networkName,
                tenantNetwork, config, appChannelAddress: agentContainerName);

            sidecarInfo = await sidecarManager.StartSidecarAsync(sidecarConfig, ct);

            var healthy = await sidecarManager.WaitForHealthyAsync(
                sidecarInfo, DefaultHealthTimeout, ct);

            if (!healthy)
            {
                throw new InvalidOperationException(
                    $"Dapr sidecar {sidecarInfo.SidecarId} did not become healthy within {DefaultHealthTimeout}.");
            }

            var augmentedEnv = new Dictionary<string, string>(
                config.EnvironmentVariables ?? new Dictionary<string, string>());
            AddDaprSidecarClientEnv(augmentedEnv, sidecarInfo);

            var augmentedConfig = config with
            {
                NetworkName = networkName,
                AdditionalNetworks = MergeAdditionalNetworks(config.AdditionalNetworks, tenantNetwork),
                EnvironmentVariables = augmentedEnv,
                ContainerName = agentContainerName,
            };

            var containerId = await containerRuntime.StartAsync(augmentedConfig, ct);

            _logger.LogInformation(
                EventIds.LifecycleCompleted,
                "Detached sidecar lifecycle started for app {AppId}, container {ContainerId}",
                appId, containerId);

            return new DetachedContainerLifecycleResult(
                containerId, sidecarInfo, networkName);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                EventIds.LifecycleFailed, ex,
                "Detached sidecar lifecycle failed for app {AppId}. Cleaning up.", appId);

            await TeardownAsync(null, sidecarInfo?.SidecarId, networkName, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Publishes how the app container reaches its sibling <c>daprd</c>. When
    /// only <c>DAPR_GRPC_PORT</c> (and HTTP port) are set, Dapr and durable
    /// workflow clients default to <c>127.0.0.1</c>, which is wrong for
    /// separate app + sidecar containers on a Podman/Docker bridge — match
    /// <c>deploy.sh</c> / <c>docker-compose</c> (<c>spring-worker-dapr:50001</c>).
    /// </summary>
    private static void AddDaprSidecarClientEnv(
        IDictionary<string, string> env,
        DaprSidecarInfo sidecar)
    {
        var host = sidecar.SidecarId;
        env["DAPR_HTTP_PORT"] = sidecar.DaprHttpPort.ToString();
        env["DAPR_GRPC_PORT"] = sidecar.DaprGrpcPort.ToString();
        env["DAPR_HTTP_ENDPOINT"] = $"http://{host}:{sidecar.DaprHttpPort}";
        env["DAPR_GRPC_ENDPOINT"] = $"http://{host}:{sidecar.DaprGrpcPort}";
    }

    private DaprSidecarConfig BuildDaprSidecarConfig(
        string appId,
        int appPort,
        int daprHttpPort,
        int daprGrpcPort,
        string primaryNetwork,
        string tenantNetwork,
        ContainerConfig appConfig,
        string? appChannelAddress = null)
    {
        // Per-launch override (Dapr Python agent) wins; else the default platform profile.
        var componentsPath = appConfig.DaprSidecarComponentsPath
            ?? _sidecarOptions.ComponentsPath;

        IReadOnlyList<string>? sidecarAdditional = null;
        if (!string.Equals(primaryNetwork, tenantNetwork, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(tenantNetwork))
        {
            // Second bridge so daprd can see placement/scheduler/Redis on the
            // tenant network after deploy.sh dual-attaches the control plane.
            sidecarAdditional = [tenantNetwork];
        }

        return new DaprSidecarConfig(
            AppId: appId,
            AppPort: appPort,
            DaprHttpPort: daprHttpPort,
            DaprGrpcPort: daprGrpcPort,
            ComponentsPath: componentsPath,
            NetworkName: primaryNetwork,
            AdditionalNetworks: sidecarAdditional,
            PlacementHostAddress: _sidecarOptions.PlacementHostAddress,
            SchedulerHostAddress: _sidecarOptions.SchedulerHostAddress,
            DaprConfigFilePath: _sidecarOptions.DaprConfigFilePath,
            AppChannelAddress: appChannelAddress);
    }

    /// <summary>
    /// Tears down the application container, Dapr sidecar, and network.
    /// Each step is best-effort to ensure maximum cleanup.
    /// </summary>
    /// <param name="containerId">The identifier of the application container to stop, or null if not started.</param>
    /// <param name="sidecarId">The identifier of the sidecar container to stop, or null if not started.</param>
    /// <param name="networkName">The network to remove, or null if not created.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public async Task TeardownAsync(string? containerId, string? sidecarId, string? networkName, CancellationToken ct = default)
    {
        _logger.LogInformation(
            EventIds.TeardownStarting,
            "Tearing down lifecycle: container={ContainerId}, sidecar={SidecarId}, network={NetworkName}",
            containerId, sidecarId, networkName);

        if (containerId is not null)
        {
            try
            {
                await containerRuntime.StopAsync(containerId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop application container {ContainerId}", containerId);
            }
        }

        if (sidecarId is not null)
        {
            try
            {
                await sidecarManager.StopSidecarAsync(sidecarId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop Dapr sidecar {SidecarId}", sidecarId);
            }
        }

        if (networkName is not null)
        {
            await RemoveNetworkAsync(networkName, ct);
        }
    }

    /// <summary>
    /// Appends the tenant network to whatever the caller already specified in
    /// <see cref="ContainerConfig.AdditionalNetworks"/>, deduplicating so a
    /// caller that already pinned the tenant network doesn't get a duplicate
    /// <c>--network</c> flag (some podman / docker versions surface a warning
    /// in that case). Order is preserved — caller-supplied networks come
    /// first, tenant network last.
    /// </summary>
    private static IReadOnlyList<string> MergeAdditionalNetworks(
        IReadOnlyList<string>? existing,
        string tenantNetwork)
    {
        if (existing is null || existing.Count == 0)
        {
            return [tenantNetwork];
        }

        if (existing.Contains(tenantNetwork, StringComparer.Ordinal))
        {
            return existing;
        }

        var merged = new List<string>(existing.Count + 1);
        merged.AddRange(existing);
        merged.Add(tenantNetwork);
        return merged;
    }

    private async Task CreateNetworkAsync(string networkName, CancellationToken ct)
    {
        _logger.LogInformation(
            EventIds.NetworkCreating,
            "Creating container network {NetworkName}", networkName);

        try
        {
            // Stage 2 of #522 routed network create through the dispatcher
            // (idempotent: a pre-existing network is treated as success).
            await containerRuntime.CreateNetworkAsync(networkName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                EventIds.NetworkCreateFailed,
                ex,
                "Failed to create network {NetworkName}", networkName);
            throw;
        }
    }

    private async Task RemoveNetworkAsync(string networkName, CancellationToken ct)
    {
        try
        {
            // Idempotent on missing — the dispatcher swallows
            // "no such network" and returns 204. The catch below covers the
            // dispatcher itself being unreachable during teardown so a
            // partial-failure boot can still complete its best-effort sweep.
            await containerRuntime.RemoveNetworkAsync(networkName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove network {NetworkName}", networkName);
        }
    }

    /// <summary>
    /// Event IDs for container lifecycle management logging (range 2200-2299).
    /// </summary>
    private static class EventIds
    {
        public static readonly EventId LifecycleStarting = new(2220, nameof(LifecycleStarting));
        public static readonly EventId LifecycleCompleted = new(2221, nameof(LifecycleCompleted));
        public static readonly EventId LifecycleFailed = new(2222, nameof(LifecycleFailed));
        public static readonly EventId TeardownStarting = new(2223, nameof(TeardownStarting));
        public static readonly EventId NetworkCreating = new(2224, nameof(NetworkCreating));
        public static readonly EventId NetworkCreateFailed = new(2225, nameof(NetworkCreateFailed));
    }
}

/// <summary>
/// Result of a container lifecycle operation including sidecar and network information.
/// </summary>
/// <param name="ContainerResult">The result of the application container execution.</param>
/// <param name="SidecarInfo">Information about the Dapr sidecar that was used.</param>
/// <param name="NetworkName">The network that was created for the lifecycle.</param>
public record ContainerLifecycleResult(
    ContainerResult ContainerResult,
    DaprSidecarInfo SidecarInfo,
    string NetworkName);

/// <summary>
/// Result of <see cref="ContainerLifecycleManager.LaunchWithSidecarDetachedAsync"/> —
/// a detached app container with its sidecar, still running.
/// </summary>
public record DetachedContainerLifecycleResult(
    string ContainerId,
    DaprSidecarInfo SidecarInfo,
    string NetworkName);