/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

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
}
