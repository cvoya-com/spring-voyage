// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

/// <summary>
/// DI-swappable governance seam that answers "should this agent act on its
/// own, propose an action, or keep watching" for a given
/// <see cref="InitiativeAction"/> and observed signal batch (#415 /
/// PR-PLAT-INIT-1). The evaluator composes every enforcement layer that a
/// self-directing agent must honour — unit-level initiative action lists
/// (#250), cost caps (#474 / #248), boundary opacity (#497 / #413),
/// hierarchy permissions (#533 / #414), and cloning policy (#536 / #416) —
/// and projects them onto the three-valued
/// <see cref="InitiativeEvaluationDecision"/>.
/// </summary>
/// <remarks>
/// <para>
/// Semantics per initiative level:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b>Passive / Attentive (Reactive baseline)</b>: the evaluator never
///     returns <see cref="InitiativeEvaluationDecision.ActAutonomously"/>.
///     It returns <see cref="InitiativeEvaluationDecision.Defer"/> for every
///     call because Reactive agents only act on direct invocation — the
///     initiative seam is not consulted on those paths.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Proactive</b>: the evaluator returns
///     <see cref="InitiativeEvaluationDecision.ActWithConfirmation"/> when
///     every policy gate passes AND the signal batch is non-empty; the agent
///     drafts a proposal for a human or the unit's approval channel. Empty
///     signal batches yield <see cref="InitiativeEvaluationDecision.Defer"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Autonomous</b>: the evaluator returns
///     <see cref="InitiativeEvaluationDecision.ActAutonomously"/> when every
///     gate passes AND the action is reversible AND the unit has not set
///     <see cref="InitiativePolicy.RequireUnitApproval"/>. Otherwise it
///     falls back to <see cref="InitiativeEvaluationDecision.ActWithConfirmation"/>
///     — a gate denial is surfaced verbatim, an unresolvable gate (the layer
///     threw) is surfaced as <c>FailedClosed = true</c>.
///     </description>
///   </item>
/// </list>
/// <para>
/// Policy is re-read on every call. Runtime changes to the unit's
/// <see cref="UnitPolicy.Initiative"/> slot or to an agent-scoped policy
/// propagate on the next evaluation — the evaluator does not cache. The
/// caching story (if any) is the responsibility of a decorating
/// implementation registered by the private cloud repo.
/// </para>
/// <para>
/// Implementations MUST be safe to call from any thread and MUST NOT throw
/// for any recognised enforcement failure — return a result with
/// <c>Decision = Defer</c> or <c>ActWithConfirmation</c> and a non-null
/// <c>Reason</c> instead. Unknown / infrastructure exceptions still escape
/// so upstream observability records them, but they first trigger a
/// fail-closed downgrade if they happen inside the compositional path.
/// </para>
/// </remarks>
public interface IAgentInitiativeEvaluator
{
    /// <summary>
    /// Evaluate whether the agent should act on the proposed
    /// <see cref="InitiativeAction"/> given the observed signals. See the
    /// interface remarks for the per-level semantics.
    /// </summary>
    /// <param name="context">The evaluation context.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The three-valued decision and its supporting metadata.</returns>
    Task<InitiativeEvaluationResult> EvaluateAsync(
        InitiativeEvaluationContext context,
        CancellationToken cancellationToken = default);
}
