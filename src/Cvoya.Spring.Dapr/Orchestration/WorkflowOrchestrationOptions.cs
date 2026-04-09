/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

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
}
