// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

using Cvoya.Spring.Core.Agents;

/// <summary>
/// Seam that encapsulates the unit-policy enforcement concern extracted from
/// <c>AgentActor</c>: evaluating model caps (#247), cost caps (#248), and
/// execution-mode policy (#249) against the per-turn effective metadata, and
/// returning either a (possibly coerced) <see cref="AgentMetadata"/> or a
/// <see cref="PolicyVerdict"/> that causes the dispatch to be refused.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host can
/// substitute a tenant-aware coordinator (e.g. one that layers audit logging
/// on every enforcement decision, or adds additional policy dimensions) without
/// touching the actor. Per the platform's "interface-first + TryAdd*" rule,
/// production DI registers the default implementation with
/// <c>TryAddSingleton</c> so the private repo's registration takes precedence
/// when present.
/// </para>
/// <para>
/// The coordinator holds zero Dapr-actor references. <see cref="ApplyUnitPoliciesAsync"/>
/// receives delegate parameters so the actor can inject its own
/// policy-evaluation implementations without the coordinator depending on Dapr
/// actor types or scoped DI services.
/// </para>
/// <para>
/// <c>IUnitPolicyEnforcer</c> is scoped — it is passed as a set of per-call
/// delegates (<paramref name="evaluateModel"/>, <paramref name="evaluateCost"/>,
/// <paramref name="resolveExecutionMode"/>) rather than injected on the
/// coordinator constructor. This matches the pattern established by
/// <see cref="IAgentObservationCoordinator.RunInitiativeCheckAsync"/>'s
/// <c>evaluateSkillPolicy</c> delegate, and prevents the singleton coordinator
/// from capturing a scoped service (which would trip
/// <c>WorkerCompositionTests.AddWorkerServices_BuildsProviderWithoutMissingRegistrations</c>
/// with <c>ValidateScopes = true</c>).
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates. This makes it safe to
/// register as a singleton and share across all <c>AgentActor</c> instances.
/// </para>
/// </remarks>
public interface IAgentUnitPolicyCoordinator
{
    /// <summary>
    /// Applies unit-level policy dimensions (#247 model, #248 cost, #249
    /// execution mode) to the per-turn effective metadata. Returns the
    /// (possibly coerced) metadata plus a non-<c>null</c>
    /// <see cref="PolicyVerdict"/> when the dispatch must be refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Model and cost deny outcomes refuse the turn — silently swapping a
    /// model mid-turn would break user expectations, and continuing past a
    /// cost cap defeats the cap's purpose. Execution-mode coercion by a
    /// forcing unit is treated as an allow (the call proceeds under the
    /// forced mode); an allow-list miss refuses the turn.
    /// </para>
    /// <para>
    /// Cost evaluation uses a projected cost of <c>0</c>: this seam does not
    /// know the prompt size yet. It is still meaningful because the enforcer
    /// sums the agent's existing window spend — a unit that already exceeded
    /// its hour / day cap will deny the turn before it runs.
    /// </para>
    /// <para>
    /// When any evaluator delegate throws a non-cancellation exception, the
    /// coordinator logs a warning and treats the outcome as allowed, to avoid
    /// losing the turn due to a transient policy-store outage.
    /// </para>
    /// </remarks>
    /// <param name="agentId">
    /// The Dapr actor id (<c>Id.GetId()</c>) of the dispatching agent. Used
    /// for structured log correlation.
    /// </param>
    /// <param name="effective">
    /// The per-turn effective <see cref="AgentMetadata"/> already resolved by
    /// the actor. The coordinator may return a coerced copy (e.g. with
    /// <see cref="AgentMetadata.ExecutionMode"/> overridden) when an
    /// execution-mode forcing policy applies.
    /// </param>
    /// <param name="evaluateModel">
    /// Delegate that evaluates the model cap for the given agent and model id.
    /// Returns a <see cref="PolicyDecision"/> indicating whether the model is
    /// allowed. Passed as a delegate so the coordinator can remain a singleton
    /// even though <c>IUnitPolicyEnforcer</c> is scoped.
    /// </param>
    /// <param name="evaluateCost">
    /// Delegate that evaluates the cost cap for the given agent and projected
    /// cost. Returns a <see cref="PolicyDecision"/>. Passed as a delegate for
    /// the same reason as <paramref name="evaluateModel"/>.
    /// </param>
    /// <param name="resolveExecutionMode">
    /// Delegate that resolves the effective execution mode for the given agent
    /// and requested mode. Returns an <see cref="ExecutionModeResolution"/>
    /// that carries both the decision and the (possibly coerced) mode. Passed
    /// as a delegate for the same reason as <paramref name="evaluateModel"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>
    /// A tuple of the (possibly coerced) <see cref="AgentMetadata"/> and a
    /// <see cref="PolicyVerdict"/> when the dispatch must be refused, or
    /// <c>null</c> when all policy dimensions pass.
    /// </returns>
    Task<(AgentMetadata Effective, PolicyVerdict? Verdict)> ApplyUnitPoliciesAsync(
        string agentId,
        AgentMetadata effective,
        Func<string, string, CancellationToken, Task<PolicyDecision>> evaluateModel,
        Func<string, decimal, CancellationToken, Task<PolicyDecision>> evaluateCost,
        Func<string, AgentExecutionMode, CancellationToken, Task<ExecutionModeResolution>> resolveExecutionMode,
        CancellationToken cancellationToken = default);
}
