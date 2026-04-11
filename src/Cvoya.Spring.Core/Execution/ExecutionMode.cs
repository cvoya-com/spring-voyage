// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

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