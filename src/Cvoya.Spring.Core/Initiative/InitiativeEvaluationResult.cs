// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

/// <summary>
/// Result of an <see cref="IAgentInitiativeEvaluator"/> call — the three-valued
/// decision plus the effective initiative level that drove it and a
/// human-readable reason. A <see cref="FailedClosed"/> flag tells the caller
/// whether an Autonomous evaluation downgraded to
/// <see cref="InitiativeEvaluationDecision.ActWithConfirmation"/> because an
/// enforcement layer could not resolve (distinct from a policy-driven
/// "Proactive-level agent, proposal required" downgrade, which is NOT a
/// fail-closed event).
/// </summary>
/// <param name="Decision">The act-now / act-with-confirmation / defer choice.</param>
/// <param name="EffectiveLevel">
/// The initiative level actually applied — after the unit-level ceiling in
/// <see cref="InitiativePolicy.MaxLevel"/> has been composed with the agent's
/// own level.
/// </param>
/// <param name="Reason">
/// Short human-readable explanation (e.g. "budget cap exceeded by unit
/// engineering", "proactive level always requires confirmation"). Present
/// for <see cref="InitiativeEvaluationDecision.Defer"/> and
/// <see cref="InitiativeEvaluationDecision.ActWithConfirmation"/>; may be
/// <c>null</c> for a clean <see cref="InitiativeEvaluationDecision.ActAutonomously"/>.
/// </param>
/// <param name="FailedClosed">
/// <c>true</c> when the result is
/// <see cref="InitiativeEvaluationDecision.ActWithConfirmation"/> because a
/// policy gate could not be evaluated and the evaluator downgraded rather
/// than silently denying. Clients (the agent runtime, observability,
/// UI proposals list) may render this differently — a fail-closed downgrade
/// typically warrants operator attention.
/// </param>
public readonly record struct InitiativeEvaluationResult(
    InitiativeEvaluationDecision Decision,
    InitiativeLevel EffectiveLevel,
    string? Reason = null,
    bool FailedClosed = false)
{
    /// <summary>
    /// Canonical defer result for Passive / Attentive agents — the Reactive
    /// baseline never acts on an initiative signal; it waits for a direct
    /// invocation.
    /// </summary>
    /// <param name="level">The agent's effective initiative level.</param>
    /// <param name="reason">Reason to record on the decision.</param>
    public static InitiativeEvaluationResult Defer(InitiativeLevel level, string reason) =>
        new(InitiativeEvaluationDecision.Defer, level, reason);

    /// <summary>
    /// Act-with-confirmation result. When the caller knows the downgrade was
    /// caused by a fail-closed gate (an enforcement layer threw or could not
    /// resolve), pass <c>failedClosed: true</c> so the runtime can surface
    /// the degraded posture.
    /// </summary>
    /// <param name="level">The agent's effective initiative level.</param>
    /// <param name="reason">Reason to record on the decision.</param>
    /// <param name="failedClosed">Whether this downgrade is a fail-closed event.</param>
    public static InitiativeEvaluationResult WithConfirmation(
        InitiativeLevel level,
        string reason,
        bool failedClosed = false) =>
        new(InitiativeEvaluationDecision.ActWithConfirmation, level, reason, failedClosed);

    /// <summary>
    /// Clean autonomous-action result — every gate passed and the agent may
    /// act without asking.
    /// </summary>
    /// <param name="level">The agent's effective initiative level.</param>
    public static InitiativeEvaluationResult Autonomously(InitiativeLevel level) =>
        new(InitiativeEvaluationDecision.ActAutonomously, level);
}
