// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

/// <summary>
/// Specifies the lifecycle operation to perform on an agent.
/// </summary>
public enum LifecycleOperation
{
    /// <summary>
    /// Create and register a new agent.
    /// </summary>
    Create,

    /// <summary>
    /// Deactivate, unregister, and clean up an existing agent.
    /// </summary>
    Delete
}