// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Diagnostics;
using Cvoya.Spring.Core.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Composes <see cref="IContainerRuntime"/> and <see cref="IDaprSidecarManager"/> to manage
/// the full lifecycle of an application container with its Dapr sidecar.
/// </summary>
public class ContainerLifecycleManager(
    IContainerRuntime containerRuntime,
    IDaprSidecarManager sidecarManager,
    IOptions<ContainerRuntimeOptions> options,
    ILoggerFactory loggerFactory)
{
    private static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = loggerFactory.CreateLogger<ContainerLifecycleManager>();
    private readonly ContainerRuntimeOptions _options = options.Value;

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
                ComponentsPath: _options.DaprComponentsPath,
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

        var (exitCode, _, stderr) = await RunProcessAsync(
            _options.RuntimeType, $"network create {networkName}", ct);

        if (exitCode != 0)
        {
            _logger.LogError(
                EventIds.NetworkCreateFailed,
                "Failed to create network {NetworkName}. Stderr: {Stderr}", networkName, stderr);
            throw new InvalidOperationException(
                $"Failed to create network {networkName}. Stderr: {stderr}");
        }
    }

    private async Task RemoveNetworkAsync(string networkName, CancellationToken ct)
    {
        try
        {
            await RunProcessAsync(_options.RuntimeType, $"network rm {networkName}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove network {NetworkName}", networkName);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
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
