// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

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
    /// Launches a container in detached mode, returning immediately with the
    /// container identifier. The container keeps running in the background
    /// until explicitly stopped via <see cref="StopAsync"/>.
    /// </summary>
    /// <param name="config">The container configuration.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The identifier of the started container.</returns>
    Task<string> StartAsync(ContainerConfig config, CancellationToken ct = default);

    /// <summary>
    /// Stops a running container by its identifier.
    /// </summary>
    /// <param name="containerId">The identifier of the container to stop.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task StopAsync(string containerId, CancellationToken ct = default);

    /// <summary>
    /// Reads the most recent log lines from a running (or recently-stopped)
    /// container. Implementations should cap the buffer at
    /// <paramref name="tail"/> lines to keep memory bounded. Used by
    /// <c>spring agent logs</c> for the persistent-agent surface (#396).
    /// </summary>
    /// <param name="containerId">The identifier of the container to read.</param>
    /// <param name="tail">Maximum number of log lines to return. Defaults to 200.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// The combined stdout+stderr tail as a single string. Returns an empty
    /// string when the container has produced no output yet. Throws if the
    /// container id is unknown so the caller can surface a 404.
    /// </returns>
    Task<string> GetLogsAsync(string containerId, int tail = 200, CancellationToken ct = default);
}

/// <summary>
/// Configuration for launching a container.
/// </summary>
/// <param name="Image">The container image to run.</param>
/// <param name="Command">An optional command to execute inside the container.</param>
/// <param name="EnvironmentVariables">Optional environment variables to set in the container.</param>
/// <param name="VolumeMounts">Optional volume mount specifications.</param>
/// <param name="Timeout">Optional timeout after which the container should be stopped.</param>
/// <param name="NetworkName">Optional Docker/Podman network to attach the container to.</param>
/// <param name="Labels">Optional container labels for identification and cleanup.</param>
/// <param name="DaprEnabled">Whether to attach a Dapr sidecar to this container.</param>
/// <param name="DaprAppId">The app-id for the Dapr sidecar.</param>
/// <param name="DaprAppPort">The port the app listens on for Dapr to call.</param>
/// <param name="ExtraHosts">Additional <c>host:IP</c> entries to add to the container's <c>/etc/hosts</c>. Used to expose the MCP server to Linux containers via <c>host.docker.internal:host-gateway</c>.</param>
/// <param name="WorkingDirectory">Optional working directory inside the container.</param>
public record ContainerConfig(
    string Image,
    string? Command = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    IReadOnlyList<string>? VolumeMounts = null,
    TimeSpan? Timeout = null,
    string? NetworkName = null,
    IReadOnlyDictionary<string, string>? Labels = null,
    bool DaprEnabled = false,
    string? DaprAppId = null,
    int? DaprAppPort = null,
    IReadOnlyList<string>? ExtraHosts = null,
    string? WorkingDirectory = null);

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