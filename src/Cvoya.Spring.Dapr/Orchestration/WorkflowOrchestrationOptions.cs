// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

/// <summary>
/// Configuration options for the workflow orchestration strategy.
/// </summary>
public class WorkflowOrchestrationOptions
{
    /// <summary>
    /// Gets or sets the container image used to run workflow orchestration.
    /// </summary>
    public string ContainerImage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum duration for workflow container execution.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or sets whether Dapr sidecar should be attached to workflow containers.
    /// </summary>
    public bool DaprEnabled { get; set; }

    /// <summary>
    /// Gets or sets the Dapr app-id for workflow containers.
    /// </summary>
    public string? DaprAppId { get; set; }

    /// <summary>
    /// Gets or sets the port the workflow container listens on for Dapr callbacks.
    /// </summary>
    public int? DaprAppPort { get; set; }
}