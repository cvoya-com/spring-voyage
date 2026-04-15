// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

using Cvoya.Spring.Core.Agents;

/// <summary>
/// DI-swappable enforcement point for <see cref="UnitPolicy"/>. The default
/// OSS implementation walks every unit the agent is a member of, consults
/// <see cref="IUnitPolicyRepository"/>, and returns the first denial (or
/// <see cref="PolicyDecision.Allowed"/> when every unit allows the action).
/// The private cloud repo replaces this with a richer implementation that
/// adds audit logging, ABAC attributes, and tenant-scoped caches — call
/// sites only depend on the interface so no downstream code has to change
/// when the richer enforcer is registered.
/// </summary>
/// <remarks>
/// <para>
/// The enforcer sits in front of every skill invocation (#163). The public
/// contract is intentionally narrow — one method per policy dimension —
/// rather than one generic <c>Evaluate(action)</c> that requires pattern
/// matching at every call site. Model caps (#247), cost caps (#248),
/// execution-mode (#249), and unit-scoped initiative policies (#250) each
/// land as their own method on this interface; future dimensions slot in the
/// same way.
/// </para>
/// <para>
/// Implementations MUST be safe to call from any thread and MUST NOT throw
/// for routine "deny" outcomes — return a <see cref="PolicyDecision"/> with
/// <c>IsAllowed = false</c> instead so callers can render a tool error
/// without wrapping the call in a try/catch.
/// </para>
/// </remarks>
public interface IUnitPolicyEnforcer
{
    /// <summary>
    /// Evaluates whether <paramref name="agentId"/> may invoke the tool
    /// named <paramref name="toolName"/>. Consults every unit the agent
    /// belongs to; if any unit's <see cref="SkillPolicy"/> denies the tool
    /// (either because the tool is in the unit's <see cref="SkillPolicy.Blocked"/>
    /// list or because the unit has a non-<c>null</c>
    /// <see cref="SkillPolicy.Allowed"/> list that does not contain the tool),
    /// the result is a deny decision whose <see cref="PolicyDecision.DenyingUnitId"/>
    /// identifies the first denying unit. When no unit denies the action,
    /// the result is <see cref="PolicyDecision.Allowed"/>.
    /// </summary>
    /// <param name="agentId">
    /// The agent's path (<c>Address.Path</c>) attempting to invoke the tool.
    /// </param>
    /// <param name="toolName">The tool being invoked.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<PolicyDecision> EvaluateSkillInvocationAsync(
        string agentId,
        string toolName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates whether <paramref name="agentId"/> may run under the model
    /// named <paramref name="modelId"/>. Consults every unit the agent
    /// belongs to; if any unit's <see cref="ModelPolicy"/> denies the model
    /// the result is a deny decision whose
    /// <see cref="PolicyDecision.DenyingUnitId"/> identifies the first denying
    /// unit (#247). Empty / whitespace model ids are treated as "allowed" so
    /// a missing model selection never blocks a dispatch — upstream validation
    /// is responsible for rejecting an empty model if that matters.
    /// </summary>
    /// <param name="agentId">The agent's path.</param>
    /// <param name="modelId">The model identifier selected for the turn.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<PolicyDecision> EvaluateModelAsync(
        string agentId,
        string modelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates whether <paramref name="agentId"/> may incur
    /// <paramref name="projectedCost"/> on its next invocation. Consults
    /// every unit the agent belongs to; if any unit's <see cref="CostPolicy"/>
    /// cap (per-invocation, per-hour, or per-day) would be exceeded, the
    /// result is a deny decision (#248). Implementations use
    /// <see cref="Costs.ICostQueryService"/> to sum existing spend in the
    /// relevant rolling window. A non-positive <paramref name="projectedCost"/>
    /// is treated as "no additional cost" — the caller is still subject to
    /// the current-window caps.
    /// </summary>
    /// <param name="agentId">The agent's path.</param>
    /// <param name="projectedCost">
    /// The estimated cost of the pending call. Pass <c>0</c> when the caller
    /// only wants to confirm that the current window has not already exceeded
    /// the cap.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<PolicyDecision> EvaluateCostAsync(
        string agentId,
        decimal projectedCost,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates whether <paramref name="agentId"/> may dispatch under the
    /// supplied <paramref name="mode"/>. Consults every unit the agent belongs
    /// to; a unit's <see cref="ExecutionModePolicy.Forced"/> mode overrides
    /// any other value — callers that support coercion should prefer
    /// <see cref="ResolveExecutionModeAsync"/> instead (#249).
    /// </summary>
    /// <param name="agentId">The agent's path.</param>
    /// <param name="mode">The execution mode proposed for the dispatch.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<PolicyDecision> EvaluateExecutionModeAsync(
        string agentId,
        AgentExecutionMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the effective execution mode for <paramref name="agentId"/>
    /// given the candidate <paramref name="mode"/>. If any unit the agent
    /// belongs to has a <see cref="ExecutionModePolicy.Forced"/> value, that
    /// value is returned. Otherwise, if any unit has a non-<c>null</c>
    /// <see cref="ExecutionModePolicy.Allowed"/> list that does not contain
    /// <paramref name="mode"/>, the mode is denied and the returned
    /// <see cref="ExecutionModeResolution.Decision"/> carries
    /// <c>IsAllowed = false</c>. When no unit constrains dispatch, the input
    /// mode is returned unchanged.
    /// </summary>
    /// <param name="agentId">The agent's path.</param>
    /// <param name="mode">The execution mode the caller would like to use.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<ExecutionModeResolution> ResolveExecutionModeAsync(
        string agentId,
        AgentExecutionMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates whether <paramref name="agentId"/> may take the reflection
    /// action named <paramref name="actionType"/>. Unit-level initiative
    /// policy is a DENY overlay on the agent's own policy (#250): a unit that
    /// blocks an action wins over the agent's own allow-list; a unit
    /// whitelist tightens the allowed set but does not broaden it.
    /// </summary>
    /// <param name="agentId">The agent's path.</param>
    /// <param name="actionType">The reflection-action type being attempted.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<PolicyDecision> EvaluateInitiativeActionAsync(
        string agentId,
        string actionType,
        CancellationToken cancellationToken = default);
}