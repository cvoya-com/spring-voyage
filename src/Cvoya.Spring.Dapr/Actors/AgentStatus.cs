// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Coarse status label for an agent actor's mailbox (#2076 / ADR-0030 §3 §44).
/// The real per-thread depth is exposed alongside this label on the
/// <c>StatusQuery</c> response payload — under concurrent threads the agent
/// may be Active on N threads simultaneously, each at its own queue depth.
/// </summary>
public enum AgentStatus
{
    /// <summary>
    /// The agent has no per-thread channels and is waiting for work.
    /// </summary>
    Idle,

    /// <summary>
    /// The agent has at least one per-thread channel with a dispatcher
    /// running or queued messages awaiting dispatch.
    /// </summary>
    Active,
}
