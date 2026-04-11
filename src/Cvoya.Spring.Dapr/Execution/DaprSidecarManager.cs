// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Diagnostics;
using System.Text;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Manages Dapr sidecar containers by launching daprd in a container on the same network
/// as the application container.
/// </summary>
public class DaprSidecarManager(
    IOptions<ContainerRuntimeOptions> options,
    ILoggerFactory loggerFactory) : IDaprSidecarManager
{
    private const string DaprImage = "daprio/daprd:latest";

    private readonly ILogger _logger = loggerFactory.CreateLogger<DaprSidecarManager>();
    private readonly ContainerRuntimeOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<DaprSidecarInfo> StartSidecarAsync(DaprSidecarConfig config, CancellationToken ct = default)
    {
        var sidecarName = $"spring-dapr-{config.AppId}-{Guid.NewGuid():N}"[..48];
        var arguments = BuildSidecarRunArguments(config, sidecarName);

        _logger.LogInformation(
            EventIds.SidecarStarting,
            "Starting Dapr sidecar {SidecarName} for app {AppId} on ports HTTP={HttpPort} gRPC={GrpcPort}",
            sidecarName, config.AppId, config.DaprHttpPort, config.DaprGrpcPort);

        var (exitCode, stdout, stderr) = await RunProcessAsync(
            _options.RuntimeType, arguments, ct);

        if (exitCode != 0)
        {
            _logger.LogError(
                EventIds.SidecarStartFailed,
                "Failed to start Dapr sidecar {SidecarName}. Exit code: {ExitCode}. Stderr: {Stderr}",
                sidecarName, exitCode, stderr);
            throw new InvalidOperationException(
                $"Failed to start Dapr sidecar {sidecarName}. Exit code: {exitCode}. Stderr: {stderr}");
        }

        var containerId = stdout.Trim();
        _logger.LogInformation(
            EventIds.SidecarStarted,
            "Dapr sidecar {SidecarName} started with container ID {ContainerId}",
            sidecarName, containerId);

        return new DaprSidecarInfo(sidecarName, config.DaprHttpPort, config.DaprGrpcPort);
    }

    /// <inheritdoc />
    public async Task StopSidecarAsync(string sidecarId, CancellationToken ct = default)
    {
        _logger.LogInformation(
            EventIds.SidecarStopping,
            "Stopping Dapr sidecar {SidecarId}", sidecarId);

        try
        {
            await RunProcessAsync(_options.RuntimeType, $"stop {sidecarId}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop Dapr sidecar {SidecarId}", sidecarId);
        }

        try
        {
            await RunProcessAsync(_options.RuntimeType, $"rm -f {sidecarId}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove Dapr sidecar {SidecarId}", sidecarId);
        }
    }

    /// <inheritdoc />
    public async Task<bool> WaitForHealthyAsync(string sidecarId, TimeSpan timeout, CancellationToken ct = default)
    {
        _logger.LogInformation(
            EventIds.SidecarHealthCheck,
            "Waiting for Dapr sidecar {SidecarId} to become healthy (timeout: {Timeout})",
            sidecarId, timeout);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // Poll the container's health by inspecting its running state.
        // In a network-attached container scenario, we check if the container is still running
        // and use docker/podman exec to hit the health endpoint.
        var pollInterval = TimeSpan.FromMilliseconds(500);

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            try
            {
                var (exitCode, _, _) = await RunProcessAsync(
                    _options.RuntimeType,
                    $"exec {sidecarId} wget -q --spider http://localhost:3500/v1.0/healthz",
                    timeoutCts.Token);

                if (exitCode == 0)
                {
                    _logger.LogInformation(
                        EventIds.SidecarHealthy,
                        "Dapr sidecar {SidecarId} is healthy", sidecarId);
                    return true;
                }
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                break;
            }

            await Task.Delay(pollInterval, timeoutCts.Token);
        }

        _logger.LogWarning(
            EventIds.SidecarUnhealthy,
            "Dapr sidecar {SidecarId} did not become healthy within {Timeout}", sidecarId, timeout);
        return false;
    }

    /// <summary>
    /// Builds the argument string for launching a Dapr sidecar container.
    /// </summary>
    internal static string BuildSidecarRunArguments(DaprSidecarConfig config, string sidecarName)
    {
        var args = new StringBuilder();
        args.Append($"run -d --name {sidecarName}");

        if (config.NetworkName is not null)
        {
            args.Append($" --network {config.NetworkName}");
        }

        args.Append($" --label spring.managed=true");
        args.Append($" --label spring.role=dapr-sidecar");
        args.Append($" --label spring.app-id={config.AppId}");

        if (config.ComponentsPath is not null)
        {
            args.Append($" -v {config.ComponentsPath}:/components");
        }

        args.Append($" {DaprImage}");
        args.Append($" ./daprd");
        args.Append($" --app-id {config.AppId}");
        args.Append($" --app-port {config.AppPort}");
        args.Append($" --dapr-http-port {config.DaprHttpPort}");
        args.Append($" --dapr-grpc-port {config.DaprGrpcPort}");

        if (config.ComponentsPath is not null)
        {
            args.Append($" --resources-path /components");
        }

        return args.ToString();
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