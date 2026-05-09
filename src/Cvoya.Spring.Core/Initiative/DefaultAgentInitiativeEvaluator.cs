// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using Cvoya.Spring.Core.Policies;

/// <summary>
/// Default OSS implementation of <see cref="IAgentInitiativeEvaluator"/>
/// (PR-PLAT-INIT-1, closes #415). Reads the agent's live initiative policy
/// (re-reads on every call — no snapshot), consults
/// <see cref="IUnitPolicyEnforcer"/> for the action allow-list and budget
/// gates, and projects the outcome onto the three-valued
/// <see cref="InitiativeEvaluationResult"/>. Private-cloud callers can
/// decorate via DI for audit / tenant scoping — every collaborator is
/// injected.
/// </summary>
/// <remarks>
/// <para>
/// Fail-closed semantics: when a policy gate throws (or returns a denial
/// the evaluator treats as unresolvable — e.g., the cost-query service is
/// unavailable), the evaluator downgrades the decision one step from
/// <see cref="InitiativeEvaluationDecision.ActAutonomously"/> to
/// <see cref="InitiativeEvaluationDecision.ActWithConfirmation"/> (with
/// <c>FailedClosed = true</c>). If the policy lookup itself fails the
/// evaluator falls all the way back to
/// <see cref="InitiativeEvaluationDecision.Defer"/>. It never silently
/// authorises an action whose gate could not be evaluated.
/// </para>
/// <para>
/// Not sealed — the private cloud repo may extend the class to add audit
/// logging or to compose additional gates (e.g., a tenant-level
/// "require MFA for autonomous actions" check). Callers that want
/// structured logging around the evaluation should wrap the evaluator with
/// a decorator; the class is kept dependency-free so
/// <c>Cvoya.Spring.Core</c> keeps its zero-external-package invariant.
/// </para>
/// </remarks>
public class DefaultAgentInitiativeEvaluator : IAgentInitiativeEvaluator
{
    private readonly IAgentPolicyStore _policyStore;
    private readonly IUnitPolicyEnforcer _unitPolicyEnforcer;

    /// <summary>
    /// Initializes a new instance of <see cref="DefaultAgentInitiativeEvaluator"/>.
    /// </summary>
    /// <param name="policyStore">Agent-scoped initiative policy store.</param>
    /// <param name="unitPolicyEnforcer">Unit-level policy enforcer for action allow-list, cost, and initiative-action composition.</param>
    public DefaultAgentInitiativeEvaluator(
        IAgentPolicyStore policyStore,
        IUnitPolicyEnforcer unitPolicyEnforcer)
    {
        ArgumentNullException.ThrowIfNull(policyStore);
        ArgumentNullException.ThrowIfNull(unitPolicyEnforcer);

        _policyStore = policyStore;
        _unitPolicyEnforcer = unitPolicyEnforcer;
    }

    /// <inheritdoc />
    public virtual async Task<InitiativeEvaluationResult> EvaluateAsync(
        InitiativeEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.AgentId);
        ArgumentNullException.ThrowIfNull(context.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Action.ActionType);

        // Always re-read policy + effective level so a runtime change (an
        // operator bumps MaxLevel from Proactive to Autonomous mid-flight)
        // takes effect on the next evaluation. No caching here.
        InitiativeLevel level;
        InitiativePolicy policy;
        try
        {
            level = await _policyStore.GetEffectiveLevelAsync(context.AgentId, cancellationToken);
            policy = await _policyStore.GetPolicyAsync(context.AgentId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // We cannot resolve the level at all → fail-closed to Defer.
            // The caller observes the reason; the agent takes no action.
            return InitiativeEvaluationResult.Defer(
                InitiativeLevel.Passive,
                $"policy lookup failed: {ex.Message}");
        }

        // Passive / Attentive — the Reactive baseline. The evaluator is
        // consulted only on self-initiated paths; for Reactive agents that
        // always means Defer.
        if (level < InitiativeLevel.Proactive)
        {
            return InitiativeEvaluationResult.Defer(
                level,
                "agent initiative level is below Proactive — reactive agents act only on direct invocation");
        }

        // Proactive and Autonomous both require at least one observed signal
        // — an empty batch means the agent has nothing to react to.
        if (context.Signals.Count == 0)
        {
            return InitiativeEvaluationResult.Defer(
                level,
                "no signals observed");
        }

        // Layer 1: unit-level action allow / block list for initiative actions.
        // Re-uses the existing enforcement seam so the policy contract stays
        // single-sourced. A deny here is authoritative — not a fail-closed
        // event, because the policy explicitly rejected the action.
        PolicyDecision actionDecision;
        try
        {
            actionDecision = await _unitPolicyEnforcer.EvaluateInitiativeActionAsync(
                context.AgentId,
                context.Action.ActionType,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InitiativeEvaluationResult.WithConfirmation(
                level,
                $"initiative action gate unresolved: {ex.Message}",
                failedClosed: true);
        }

        if (!actionDecision.IsAllowed)
        {
            // A hard deny from the action gate — downgrade to confirmation
            // (never silently deny; the operator still sees the proposal so
            // they can flip the policy if it was configured in error).
            return InitiativeEvaluationResult.WithConfirmation(
                level,
                actionDecision.Reason ?? "action blocked by unit initiative policy");
        }

        // Layer 2: cost / budget. The cost enforcer covers the per-invocation
        // + per-hour + per-day caps contributed by PR #474 (#248). We only
        // forward the estimated cost the caller supplied; zero is a valid
        // input (the enforcer still checks current-window spend). A raw
        // deny is a real denial; any exception inside the enforcer is
        // fail-closed.
        PolicyDecision costDecision;
        try
        {
            costDecision = await _unitPolicyEnforcer.EvaluateCostAsync(
                context.AgentId,
                context.Action.EstimatedCost,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InitiativeEvaluationResult.WithConfirmation(
                level,
                $"cost gate unresolved: {ex.Message}",
                failedClosed: true);
        }

        if (!costDecision.IsAllowed)
        {
            return InitiativeEvaluationResult.WithConfirmation(
                level,
                costDecision.Reason ?? "projected cost exceeds unit cap");
        }

        // Proactive always requires a human-visible proposal — that IS the
        // definition of the level. It never escalates to ActAutonomously
        // regardless of how green every other gate is.
        if (level < InitiativeLevel.Autonomous)
        {
            return InitiativeEvaluationResult.WithConfirmation(
                level,
                "proactive level always requires confirmation");
        }

        // Unit policy may still require approval for every initiative action
        // even when the agent itself is autonomous — this is a deliberate
        // operator override, not a fail-closed downgrade.
        if (policy.RequireUnitApproval)
        {
            return InitiativeEvaluationResult.WithConfirmation(
                level,
                "unit policy requires approval for every initiative action");
        }

        // Irreversible actions never run autonomously. The Autonomous level
        // is defined as "acts without confirmation on reversible /
        // in-budget actions" — anything else stays in confirmation mode.
        if (!context.Action.IsReversible)
        {
            return InitiativeEvaluationResult.WithConfirmation(
                level,
                "action is not marked as reversible");
        }

        return InitiativeEvaluationResult.Autonomously(level);
    }
}
