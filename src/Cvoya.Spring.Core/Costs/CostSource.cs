// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Costs;

/// <summary>
/// Identifies whether a cost record originated from an agent's normal
/// conversation work or from its initiative (Tier 2 reflection) loop.
/// Tagged at emission time by <c>AgentActor</c> so the cost API can
/// return an authoritative split instead of leaving the classification
/// to client-side heuristics.
/// </summary>
public enum CostSource
{
    /// <summary>
    /// Cost attributable to normal agent work: conversation responses,
    /// tool invocations, and other message-driven execution.
    /// </summary>
    Work = 0,

    /// <summary>
    /// Cost attributable to the initiative loop (reflection / proactive
    /// decisioning via the Tier 2 cognition provider).
    /// </summary>
    Initiative = 1,
}