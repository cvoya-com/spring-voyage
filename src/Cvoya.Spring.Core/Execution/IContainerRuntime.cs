/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Abstraction for running agent workloads in containers.
/// </summary>
public interface IContainerRuntime
{
    /// <summary>
    /// Launches a container with the given configuration and waits for it to complete.
    /// </summary>
    /// <param name="config">The container configuration.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The result of the container execution.</returns>
    Task<ContainerResult> RunAsync(ContainerConfig config, CancellationToken ct = default);

    /// <summary>
    /// Stops a running container by its identifier.
    /// </summary>
    /// <param name="containerId">The identifier of the container to stop.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task StopAsync(string containerId, CancellationToken ct = default);
}

/// <summary>
/// Configuration for launching a container.
/// </summary>
/// <param name="Image">The container image to run.</param>
/// <param name="Command">An optional command to execute inside the container.</param>
/// <param name="EnvironmentVariables">Optional environment variables to set in the container.</param>
/// <param name="VolumeMounts">Optional volume mount specifications.</param>
/// <param name="Timeout">Optional timeout after which the container should be stopped.</param>
public record ContainerConfig(
    string Image,
    string? Command = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    IReadOnlyList<string>? VolumeMounts = null,
    TimeSpan? Timeout = null);

/// <summary>
/// Result of a container execution.
/// </summary>
/// <param name="ContainerId">The identifier of the container that ran.</param>
/// <param name="ExitCode">The exit code returned by the container process.</param>
/// <param name="StandardOutput">The standard output captured from the container.</param>
/// <param name="StandardError">The standard error captured from the container.</param>
public record ContainerResult(
    string ContainerId,
    int ExitCode,
    string StandardOutput,
    string StandardError);
