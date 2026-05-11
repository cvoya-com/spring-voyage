// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Agents;

/// <summary>
/// Coarse activation outcome for an agent artefact installed from a package
/// or created directly (#2156). Distinct from
/// <see cref="AgentRuntimeStatus"/> — that one is the moment-to-moment
/// mailbox / container snapshot the portal renders; this one is the
/// durable installation outcome. An agent in <see cref="Error"/> failed
/// directory registration or definition persistence at install time and
/// will not pick up messages until the operator re-installs.
/// </summary>
public enum AgentLifecycleStatus
{
    /// <summary>
    /// The agent was successfully installed: directory entry registered,
    /// any execution / AI definition JSON persisted. The default for any
    /// agent that has never had a lifecycle status persisted (e.g. agents
    /// installed before #2156 landed) — those activations completed
    /// successfully in the legacy path so the default is the correct
    /// backwards-compatible answer.
    /// </summary>
    Active,

    /// <summary>
    /// The agent's installation failed (definition parse failed,
    /// directory registration threw, or the definition JSON write
    /// threw). The companion error string carries the diagnostic so the
    /// operator can fix the underlying problem and re-install.
    /// </summary>
    Error,
}
