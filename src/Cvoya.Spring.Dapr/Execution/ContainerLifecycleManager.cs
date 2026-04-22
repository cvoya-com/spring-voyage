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

        _logger.LogInformation(
            EventIds.LifecycleStarting,
            "Starting container lifecycle for app {AppId} on network {NetworkName}",
            appId, networkName);

        // Step 1: Create the network.
        await CreateNetworkAsync(networkName, ct);

        DaprSidecarInfo? sidecarInfo = null;
        try
        {
            // Step 2: Start the Dapr sidecar.
            var sidecarConfig = new DaprSidecarConfig(
                AppId: appId,
                AppPort: appPort,
                DaprHttpPort: daprHttpPort,
                DaprGrpcPort: daprGrpcPort,
                ComponentsPath: _sidecarOptions.ComponentsPath,
                NetworkName: networkName);

            sidecarInfo = await sidecarManager.StartSidecarAsync(sidecarConfig, ct);

            // Step 3: Wait for sidecar health.
            var healthy = await sidecarManager.WaitForHealthyAsync(
                sidecarInfo.SidecarId, DefaultHealthTimeout, ct);

            if (!healthy)
            {
                throw new InvalidOperationException(
                    $"Dapr sidecar {sidecarInfo.SidecarId} did not become healthy within {DefaultHealthTimeout}.");
            }

            // Step 4: Augment the app config with Dapr env vars and network.
            var augmentedEnv = new Dictionary<string, string>(
                config.EnvironmentVariables ?? new Dictionary<string, string>())
            {
                ["DAPR_HTTP_PORT"] = daprHttpPort.ToString(),
                ["DAPR_GRPC_PORT"] = daprGrpcPort.ToString()
            };

            var augmentedConfig = config with
            {
                NetworkName = networkName,
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