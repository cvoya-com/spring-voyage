// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Manages Dapr sidecar containers alongside application containers.
/// </summary>
public interface IDaprSidecarManager
{
    /// <summary>
    /// Starts a Dapr sidecar container with the given configuration.
    /// </summary>
    /// <param name="config">The sidecar configuration.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>Information about the started sidecar.</returns>
    Task<DaprSidecarInfo> StartSidecarAsync(DaprSidecarConfig config, CancellationToken ct = default);

    /// <summary>
    /// Stops and removes a Dapr sidecar container.
    /// </summary>
    /// <param name="sidecarId">The identifier of the sidecar container to stop.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task StopSidecarAsync(string sidecarId, CancellationToken ct = default);

    /// <summary>
    /// Waits for the Dapr sidecar to become healthy.
    /// </summary>
    /// <param name="sidecarId">The identifier of the sidecar container.</param>
    /// <param name="timeout">The maximum time to wait for the sidecar to become healthy.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>True if the sidecar became healthy within the timeout; false otherwise.</returns>
    Task<bool> WaitForHealthyAsync(string sidecarId, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Configuration for launching a Dapr sidecar container.
/// </summary>
/// <param name="AppId">The Dapr app-id for the sidecar.</param>
/// <param name="AppPort">The port the application listens on for Dapr to call.</param>
/// <param name="DaprHttpPort">The HTTP port for the Dapr sidecar API.</param>
/// <param name="DaprGrpcPort">The gRPC port for the Dapr sidecar API.</param>
/// <param name="ComponentsPath">The path to the Dapr components directory.</param>
/// <param name="NetworkName">The Docker/Podman network to attach the sidecar to.</param>
public record DaprSidecarConfig(
    string AppId,
    int AppPort,
    int DaprHttpPort,
    int DaprGrpcPort,
    string? ComponentsPath = null,
    string? NetworkName = null);

/// <summary>
/// Information about a running Dapr sidecar container.
/// </summary>
/// <param name="SidecarId">The identifier of the sidecar container.</param>
/// <param name="DaprHttpPort">The HTTP port of the sidecar API.</param>
/// <param name="DaprGrpcPort">The gRPC port of the sidecar API.</param>
public record DaprSidecarInfo(
    string SidecarId,
    int DaprHttpPort,
    int DaprGrpcPort);
