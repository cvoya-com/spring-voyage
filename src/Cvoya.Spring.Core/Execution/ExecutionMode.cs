/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Defines the execution modes for agent work.
/// </summary>
public enum ExecutionMode
{
    /// <summary>
    /// The agent runs within the Spring Voyage host process.
    /// </summary>
    Hosted,

    /// <summary>
    /// The agent runs in an external execution environment (e.g., a container).
    /// </summary>
    Delegated
}
