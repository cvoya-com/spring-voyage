// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

/// <summary>
/// Three-valued outcome of <see cref="IAgentInitiativeEvaluator"/>: act now
/// without asking, propose the action to a human first, or do nothing yet.
/// </summary>
/// <remarks>
/// Modelled as three values (rather than a boolean allow / deny) because the
/// Autonomous level has two "allowed" modes — fully autonomous execution for
/// reversible in-budget actions, and a confirmation-required fallback when
/// any enforcement layer cannot resolve cleanly (fail closed). A Proactive
/// agent never reaches <see cref="ActAutonomously"/>; it always proposes via
/// <see cref="ActWithConfirmation"/> even when every gate is green.
/// </remarks>
public enum InitiativeEvaluationDecision
{
    /// <summary>
    /// Do not act on this signal. Either the agent's initiative level is
    /// Passive / Attentive (Reactive baseline — waits for a direct invocation),
    /// no signals justified an action, or the signals did not yet cross the
    /// rate-limit / cost threshold. The caller should keep the event on the
    /// observation stream but take no action.
    /// </summary>
    Defer,

    /// <summary>
    /// The agent should act on this signal but MUST first surface a proposal
    /// to a human (or to the unit's approval channel). Returned for every
    /// Proactive evaluation that passes the policy gates, and for Autonomous
    /// evaluations that fail-closed — one of the enforcement layers was
    /// unresolvable, the action is not reversible, or unit policy requires
    /// unit-level approval.
    /// </summary>
    ActWithConfirmation,

    /// <summary>
    /// The agent may act immediately without asking for confirmation.
    /// Returned only for Autonomous agents on reversible actions that every
    /// enforcement layer (action allow-list, budget, boundary, hierarchy
    /// permissions, cloning policy) explicitly permitted.
    /// </summary>
    ActAutonomously,
}
