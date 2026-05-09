// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

/// <summary>
/// Configuration options for container runtime execution.
/// </summary>
public class ContainerRuntimeOptions
{
    /// <summary>
    /// Gets or sets the container runtime type. Supported values: "podman", "docker".
    /// </summary>
    public string RuntimeType { get; set; } = "podman";

    /// <summary>
    /// Gets or sets the default timeout for container execution.
    /// </summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or sets the optional network mode for containers (e.g., "host", "bridge").
    /// </summary>
    public string? NetworkMode { get; set; }

    /// <summary>
    /// Gets or sets the path to the Dapr components directory for sidecar containers.
    /// </summary>
    public string? DaprComponentsPath { get; set; }

    /// <summary>
    /// Gets or sets the default Docker/Podman network name for container lifecycle operations.
    /// </summary>
    public string? DefaultNetworkName { get; set; }
}
