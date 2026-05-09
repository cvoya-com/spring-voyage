// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Represents the current status of an agent actor.
/// </summary>
public enum AgentStatus
{
    /// <summary>
    /// The agent has no active threads and is waiting for work.
    /// </summary>
    Idle,

    /// <summary>
    /// The agent has an active thread being processed.
    /// </summary>
    Active,

    /// <summary>
    /// The agent's active thread has been suspended (e.g., due to a higher-priority thread).
    /// </summary>
    Suspended
}
