// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Diagnostics;
using System.Text;
using Cvoya.Spring.Core.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Base container runtime that shells out to a CLI binary (podman or docker) via <see cref="Process"/>.
/// </summary>
public class ProcessContainerRuntime(
    string binaryName,
    IOptions<ContainerRuntimeOptions> options,
    ILoggerFactory loggerFactory) : IContainerRuntime
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ProcessContainerRuntime>();
    private readonly ContainerRuntimeOptions _options = options.Value;

    /// <summary>
    /// Launches a container using the configured CLI binary and waits for it to complete.
    /// </summary>
    /// <param name="config">The container configuration.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The result of the container execution.</returns>
    public async Task<ContainerResult> RunAsync(ContainerConfig config, CancellationToken ct = default)
    {
        var containerName = $"spring-exec-{Guid.NewGuid():N}";
        var arguments = BuildRunArguments(config, containerName);

        _logger.LogInformation(
            "Starting container {ContainerName} with image {Image} using {Binary}",
            containerName, config.Image, binaryName);

        var timeout = config.Timeout ?? _options.DefaultTimeout;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        try
        {
            var (exitCode, stdout, stderr) = await RunProcessAsync(
                binaryName, arguments, timeoutCts.Token);

            _logger.LogInformation(
                "Container {ContainerName} exited with code {ExitCode}",
                containerName, exitCode);

            return new ContainerResult(containerName, exitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout expired — stop the container.
            _logger.LogWarning("Container {ContainerName} timed out after {Timeout}", containerName, timeout);
            await StopAsync(containerName, CancellationToken.None);
            throw new TimeoutException($"Container {containerName} exceeded timeout of {timeout}.");
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — stop the container.
            _logger.LogWarning("Container {ContainerName} was cancelled", containerName);
            await StopAsync(containerName, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Stops and removes a container by its identifier.
    /// </summary>
    /// <param name="containerId">The identifier of the container to stop.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public async Task StopAsync(string containerId, CancellationToken ct = default)
    {
        _logger.LogInformation("Stopping container {ContainerId} using {Binary}", containerId, binaryName);

        try
        {
            await RunProcessAsync(binaryName, $"stop {containerId}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop container {ContainerId}", containerId);
        }

        try
        {
            await RunProcessAsync(binaryName, $"rm -f {containerId}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove container {ContainerId}", containerId);
        }
    }

    /// <summary>
    /// Builds the argument string for the container run command.
    /// </summary>
    /// <param name="config">The container configuration.</param>
    /// <param name="containerName">The unique container name.</param>
    /// <returns>The arguments string for the run command.</returns>
    internal static string BuildRunArguments(ContainerConfig config, string containerName)
    {
        var args = new StringBuilder();
        args.Append($"run --rm --name {containerName}");

        if (config.NetworkName is not null)
        {
            args.Append($" --network {config.NetworkName}");
        }

        if (config.Labels is not null)
        {
            foreach (var (key, value) in config.Labels)
            {
                args.Append($" --label {key}={value}");
            }
        }

        if (config.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in config.EnvironmentVariables)
            {
                args.Append($" -e {key}={value}");
            }
        }

        if (config.VolumeMounts is not null)
        {
            foreach (var mount in config.VolumeMounts)
            {
                args.Append($" -v {mount}");
            }
        }

        args.Append($" {config.Image}");

        if (config.Command is not null)
        {
            args.Append($" {config.Command}");
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

        // Read stdout and stderr concurrently to avoid deadlocks.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }
}
