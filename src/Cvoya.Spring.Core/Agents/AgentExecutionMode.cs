// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Agents;

/// <summary>
/// How an agent participates in message dispatch within its containing unit.
/// This is agent-level configuration — the agent owns its execution mode;
/// the unit does not override it (see #163 for the separate unit-level
/// policy-enforcement surface, which can further restrict but not extend).
/// </summary>
public enum AgentExecutionMode
{
    /// <summary>
    /// The containing unit's orchestration strategy may route domain messages
    /// to this agent without explicit addressing. Default for new agents.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// The agent receives messages only when an inbound message addresses
    /// it explicitly. Useful for specialists that should not be woken by
    /// generic traffic.
    /// </summary>
    OnDemand = 1,
}