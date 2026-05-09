// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

/// <summary>
/// Describes a proposed agent-initiated action that an
/// <see cref="IAgentInitiativeEvaluator"/> is asked to evaluate. The shape is
/// intentionally minimal — the evaluator only needs enough to compose the
/// existing policy gates (action allow/block list, budget, boundary,
/// hierarchy permissions, cloning).
/// </summary>
/// <param name="ActionType">
/// The action-type identifier the agent proposes to perform (e.g.,
/// <c>send-message</c>, <c>start-conversation</c>, <c>clone</c>). Matched
/// case-insensitively by the downstream policy enforcer.
/// </param>
/// <param name="EstimatedCost">
/// Projected cost (in dollars) of the proposed action, used to feed the
/// per-invocation / per-hour / per-day cost caps in
/// <see cref="Costs.CostPolicy"/>. Callers that cannot estimate cost should
/// pass <c>0</c>; the evaluator still checks whether the current window has
/// already breached the cap.
/// </param>
/// <param name="IsReversible">
/// <c>true</c> when the proposed action is safe to revert (e.g., drafting a
/// message, writing to a scratchpad). Autonomous agents may act on reversible
/// actions without confirmation; irreversible actions always downgrade to
/// <see cref="InitiativeEvaluationDecision.ActWithConfirmation"/> regardless
/// of initiative level.
/// </param>
/// <param name="TargetAddress">
/// Optional destination the action would write to (e.g., the conversation or
/// agent address for a <c>send-message</c> action). Consulted by the
/// boundary / hierarchy-permission gates when the action targets a resource
/// outside the agent's own unit.
/// </param>
public record InitiativeAction(
    string ActionType,
    decimal EstimatedCost = 0m,
    bool IsReversible = true,
    string? TargetAddress = null);
