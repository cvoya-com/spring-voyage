// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

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
/// matching at every call site. New dimensions (model caps, cost caps,
/// execution mode, initiative) will each land as a new method on this
/// interface as they are promoted from the roadmap.
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
}