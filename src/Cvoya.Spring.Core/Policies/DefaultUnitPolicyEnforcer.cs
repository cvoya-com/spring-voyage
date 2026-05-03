// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Initiative;
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
/// Evaluation rules per dimension:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b>Skill</b> (#163): a tool in <see cref="SkillPolicy.Blocked"/> is
///     always denied; when <see cref="SkillPolicy.Allowed"/> is non-<c>null</c>,
///     only members of the list are permitted.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Model</b> (#247): mirrors skill — block-list wins, then whitelist.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Cost</b> (#248): each cap is checked against the current window sum
///     obtained from <see cref="ICostQueryService"/> plus <c>projectedCost</c>.
///     The tightest breached cap wins.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>ExecutionMode</b> (#249): a forcing unit coerces the mode;
///     otherwise a non-<c>null</c> allow-list denies modes outside it.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Initiative</b> (#250): unit-level
///     <see cref="InitiativePolicy.BlockedActions"/> / <see cref="InitiativePolicy.AllowedActions"/>
///     layer as a DENY overlay over the agent-level policy. Callers that want
///     the agent-level gate to also apply must evaluate it themselves — the
///     enforcer only speaks for the unit.
///     </description>
///   </item>
/// </list>
/// <para>
/// Matching of string identifiers (tool names, model ids, action types) is
/// case-insensitive throughout for parity with <see cref="SkillPolicy"/>.
/// </para>
/// </remarks>
public class DefaultUnitPolicyEnforcer(
    IUnitMembershipRepository memberships,
    IUnitPolicyRepository policies,
    ICostQueryService? costQueries = null,
    TimeProvider? timeProvider = null) : IUnitPolicyEnforcer
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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

        return await EvaluateAcrossUnitsAsync(agentId, (policy, unitId) =>
        {
            if (policy.Skill is null)
            {
                return PolicyDecision.Allowed;
            }

            return EvaluateSkillPolicy(policy.Skill, toolName, unitId);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<PolicyDecision> EvaluateModelAsync(
        string agentId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(modelId))
        {
            return PolicyDecision.Allowed;
        }

        return await EvaluateAcrossUnitsAsync(agentId, (policy, unitId) =>
        {
            if (policy.Model is null)
            {
                return PolicyDecision.Allowed;
            }

            return EvaluateModelPolicy(policy.Model, modelId, unitId);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<PolicyDecision> EvaluateCostAsync(
        string agentId,
        decimal projectedCost,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return PolicyDecision.Allowed;
        }

        if (!Guid.TryParse(agentId, out var agentUuid))
        {
            return PolicyDecision.Allowed;
        }

        var agentMemberships = await memberships.ListByAgentAsync(agentUuid, cancellationToken);
        if (agentMemberships.Count == 0)
        {
            return PolicyDecision.Allowed;
        }

        // Pre-check the per-invocation cap first — it does not depend on
        // window sums, so we can short-circuit without a database call.
        foreach (var membership in agentMemberships)
        {
            var unitIdStr = membership.UnitId.ToString();
            var policy = await policies.GetAsync(membership.UnitId, cancellationToken);
            if (policy.Cost?.MaxCostPerInvocation is { } perCall &&
                projectedCost > perCall)
            {
                return PolicyDecision.Deny(
                    $"Projected cost {projectedCost:C} exceeds per-invocation cap " +
                    $"{perCall:C} for unit '{unitIdStr}'.",
                    unitIdStr);
            }
        }

        // Window-based caps require a cost-query service. Missing service =
        // the host does not persist CostRecord entries; fall through to allow
        // so a test harness does not turn every dispatch into a denial.
        if (costQueries is null)
        {
            return PolicyDecision.Allowed;
        }

        var now = _timeProvider.GetUtcNow();
        decimal? hourlySum = null;
        decimal? dailySum = null;

        foreach (var membership in agentMemberships)
        {
            var unitIdStr = membership.UnitId.ToString();
            var policy = await policies.GetAsync(membership.UnitId, cancellationToken);
            if (policy.Cost is null)
            {
                continue;
            }

            if (policy.Cost.MaxCostPerHour is { } perHour)
            {
                hourlySum ??= (await costQueries.GetAgentCostAsync(
                    agentUuid, now.AddHours(-1), now, cancellationToken)).TotalCost;

                if (hourlySum.Value + projectedCost > perHour)
                {
                    return PolicyDecision.Deny(
                        $"Hourly spend {hourlySum.Value:C} + projected {projectedCost:C} " +
                        $"exceeds per-hour cap {perHour:C} for unit '{unitIdStr}'.",
                        unitIdStr);
                }
            }

            if (policy.Cost.MaxCostPerDay is { } perDay)
            {
                dailySum ??= (await costQueries.GetAgentCostAsync(
                    agentUuid, now.AddDays(-1), now, cancellationToken)).TotalCost;

                if (dailySum.Value + projectedCost > perDay)
                {
                    return PolicyDecision.Deny(
                        $"Daily spend {dailySum.Value:C} + projected {projectedCost:C} " +
                        $"exceeds per-day cap {perDay:C} for unit '{unitIdStr}'.",
                        unitIdStr);
                }
            }
        }

        return PolicyDecision.Allowed;
    }

    /// <inheritdoc />
    public virtual async Task<PolicyDecision> EvaluateExecutionModeAsync(
        string agentId,
        AgentExecutionMode mode,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveExecutionModeAsync(agentId, mode, cancellationToken);
        if (!resolution.Decision.IsAllowed)
        {
            return resolution.Decision;
        }

        return resolution.Mode == mode
            ? PolicyDecision.Allowed
            : PolicyDecision.Deny(
                $"Execution mode '{mode}' is coerced to '{resolution.Mode}' by unit policy.",
                resolution.Decision.DenyingUnitId);
    }

    /// <inheritdoc />
    public virtual async Task<ExecutionModeResolution> ResolveExecutionModeAsync(
        string agentId,
        AgentExecutionMode mode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return ExecutionModeResolution.AllowAsIs(mode);
        }

        if (!Guid.TryParse(agentId, out var agentUuid))
        {
            return ExecutionModeResolution.AllowAsIs(mode);
        }

        var agentMemberships = await memberships.ListByAgentAsync(agentUuid, cancellationToken);
        if (agentMemberships.Count == 0)
        {
            return ExecutionModeResolution.AllowAsIs(mode);
        }

        // First pass: any unit that forces a mode wins — coercion is strongest.
        foreach (var membership in agentMemberships)
        {
            var policy = await policies.GetAsync(membership.UnitId, cancellationToken);
            if (policy.ExecutionMode?.Forced is { } forced)
            {
                return new ExecutionModeResolution(PolicyDecision.Allowed, forced);
            }
        }

        // Second pass: whitelist-only. A mode outside every non-null
        // allow-list is denied.
        foreach (var membership in agentMemberships)
        {
            var unitIdStr = membership.UnitId.ToString();
            var policy = await policies.GetAsync(membership.UnitId, cancellationToken);
            if (policy.ExecutionMode?.Allowed is { Count: > 0 } allowed &&
                !allowed.Contains(mode))
            {
                return new ExecutionModeResolution(
                    PolicyDecision.Deny(
                        $"Execution mode '{mode}' is not permitted by unit '{unitIdStr}'.",
                        unitIdStr),
                    mode);
            }
        }

        return ExecutionModeResolution.AllowAsIs(mode);
    }

    /// <inheritdoc />
    public virtual async Task<PolicyDecision> EvaluateInitiativeActionAsync(
        string agentId,
        string actionType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(actionType))
        {
            return PolicyDecision.Allowed;
        }

        return await EvaluateAcrossUnitsAsync(agentId, (policy, unitId) =>
        {
            if (policy.Initiative is null)
            {
                return PolicyDecision.Allowed;
            }

            return EvaluateInitiativePolicy(policy.Initiative, actionType, unitId);
        }, cancellationToken);
    }

    private async Task<PolicyDecision> EvaluateAcrossUnitsAsync(
        string agentId,
        Func<UnitPolicy, string, PolicyDecision> evaluator,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(agentId, out var agentUuid))
        {
            // agentId is not a UUID — cannot resolve memberships.
            return PolicyDecision.Allowed;
        }

        var agentMemberships = await memberships.ListByAgentAsync(agentUuid, cancellationToken);

        if (agentMemberships.Count == 0)
        {
            return PolicyDecision.Allowed;
        }

        foreach (var membership in agentMemberships)
        {
            // Policy repo uses the unit's stable UUID as key.
            var unitIdStr = membership.UnitId.ToString();
            var policy = await policies.GetAsync(membership.UnitId, cancellationToken);
            var decision = evaluator(policy, unitIdStr);
            if (!decision.IsAllowed)
            {
                return decision;
            }
        }

        return PolicyDecision.Allowed;
    }

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

    /// <summary>
    /// Pure evaluation of a single <see cref="ModelPolicy"/> against a model
    /// identifier. Mirrors <see cref="EvaluateSkillPolicy"/> — block-list wins
    /// over allow-list; matching is case-insensitive.
    /// </summary>
    /// <param name="policy">The model policy to evaluate.</param>
    /// <param name="modelId">The model identifier being selected.</param>
    /// <param name="unitId">The unit id to record on deny decisions.</param>
    protected static PolicyDecision EvaluateModelPolicy(
        ModelPolicy policy,
        string modelId,
        string unitId)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);

        if (policy.Blocked is { Count: > 0 } blocked &&
            blocked.Any(b => string.Equals(b, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            return PolicyDecision.Deny(
                $"Model '{modelId}' is blocked by unit '{unitId}' model policy.",
                unitId);
        }

        if (policy.Allowed is { } allowed &&
            !allowed.Any(a => string.Equals(a, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            return PolicyDecision.Deny(
                $"Model '{modelId}' is not in unit '{unitId}' allowed-models list.",
                unitId);
        }

        return PolicyDecision.Allowed;
    }

    /// <summary>
    /// Pure evaluation of a unit-scoped <see cref="InitiativePolicy"/> against
    /// a reflection-action type. Only <see cref="InitiativePolicy.BlockedActions"/>
    /// and <see cref="InitiativePolicy.AllowedActions"/> are consulted — the
    /// other fields (tier configs, max level) are reserved for the agent-level
    /// policy and are not re-enforced here.
    /// </summary>
    /// <param name="policy">The initiative policy to evaluate.</param>
    /// <param name="actionType">The action-type string being attempted.</param>
    /// <param name="unitId">The unit id to record on deny decisions.</param>
    protected static PolicyDecision EvaluateInitiativePolicy(
        InitiativePolicy policy,
        string actionType,
        string unitId)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);

        if (policy.BlockedActions is { Count: > 0 } blocked &&
            blocked.Any(b => string.Equals(b, actionType, StringComparison.OrdinalIgnoreCase)))
        {
            return PolicyDecision.Deny(
                $"Action '{actionType}' is blocked by unit '{unitId}' initiative policy.",
                unitId);
        }

        if (policy.AllowedActions is { Count: > 0 } allowed &&
            !allowed.Any(a => string.Equals(a, actionType, StringComparison.OrdinalIgnoreCase)))
        {
            return PolicyDecision.Deny(
                $"Action '{actionType}' is not in unit '{unitId}' allowed-actions list.",
                unitId);
        }

        return PolicyDecision.Allowed;
    }

}