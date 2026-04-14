// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

using Cvoya.Spring.Core.Units;

/// <summary>
/// Default OSS implementation of <see cref="IUnitPolicyEnforcer"/>. Looks up
/// every unit the agent belongs to via <see cref="IUnitMembershipRepository"/>,
/// loads each unit's policy via <see cref="IUnitPolicyRepository"/>, and
/// returns the first deny decision or <see cref="PolicyDecision.Allowed"/>
/// when no unit denies the action.
/// </summary>
/// <remarks>
/// <para>
/// Kept in <c>Cvoya.Spring.Core</c> so the private cloud repo can pre-register
/// a tenant-scoped / audit-logging wrapper via DI without taking a dependency
/// on <c>Cvoya.Spring.Dapr</c>. Not sealed — subclasses may extend
/// evaluation (e.g. to layer per-agent overrides) by calling the base
/// implementation and then either short-circuiting or tightening the decision.
/// </para>
/// <para>
/// Skill-policy evaluation rules (#163): a tool name in a unit's
/// <see cref="SkillPolicy.Blocked"/> list is always denied. When a unit's
/// <see cref="SkillPolicy.Allowed"/> list is non-<c>null</c>, only members of
/// the list are permitted. Matching is case-insensitive. Per-membership
/// overrides never loosen the unit policy — if the unit blocks a skill, no
/// agent in that unit can use it regardless of their own declaration.
/// </para>
/// </remarks>
public class DefaultUnitPolicyEnforcer(
    IUnitMembershipRepository memberships,
    IUnitPolicyRepository policies) : IUnitPolicyEnforcer
{
    /// <inheritdoc />
    public virtual async Task<PolicyDecision> EvaluateSkillInvocationAsync(
        string agentId,
        string toolName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(toolName))
        {
            // An empty agent / tool cannot be evaluated meaningfully; treat
            // as allow so we never break a call path with a nonsense input.
            // Upstream validation is responsible for rejecting empty names.
            return PolicyDecision.Allowed;
        }

        var agentMemberships = await memberships
            .ListByAgentAsync(agentId, cancellationToken);

        if (agentMemberships.Count == 0)
        {
            // Agent is not a member of any unit — no unit policy applies.
            // Back-compat with the pre-#162 world where nothing restricted skills.
            return PolicyDecision.Allowed;
        }

        foreach (var membership in agentMemberships)
        {
            var policy = await policies.GetAsync(membership.UnitId, cancellationToken);
            if (policy.Skill is null)
            {
                continue;
            }

            var decision = EvaluateSkillPolicy(policy.Skill, toolName, membership.UnitId);
            if (!decision.IsAllowed)
            {
                return decision;
            }
        }

        return PolicyDecision.Allowed;
    }

    /// <summary>
    /// Pure evaluation of a single <see cref="SkillPolicy"/> against a tool
    /// name. Exposed as <c>protected</c> so subclasses can reuse the rule
    /// engine when they compose additional checks.
    /// </summary>
    /// <param name="policy">The skill policy to evaluate.</param>
    /// <param name="toolName">The tool being invoked.</param>
    /// <param name="unitId">The unit id to record on deny decisions.</param>
    protected static PolicyDecision EvaluateSkillPolicy(
        SkillPolicy policy,
        string toolName,
        string unitId)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);

        if (policy.Blocked is { Count: > 0 } blocked &&
            blocked.Any(b => string.Equals(b, toolName, StringComparison.OrdinalIgnoreCase)))
        {
            return PolicyDecision.Deny(
                $"Tool '{toolName}' is blocked by unit '{unitId}' skill policy.",
                unitId);
        }

        if (policy.Allowed is { } allowed &&
            !allowed.Any(a => string.Equals(a, toolName, StringComparison.OrdinalIgnoreCase)))
        {
            return PolicyDecision.Deny(
                $"Tool '{toolName}' is not in unit '{unitId}' allowed-skills list.",
                unitId);
        }

        return PolicyDecision.Allowed;
    }
}