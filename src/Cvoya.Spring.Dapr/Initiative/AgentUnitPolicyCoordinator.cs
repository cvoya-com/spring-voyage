// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Policies;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IAgentUnitPolicyCoordinator"/>.
/// Owns the unit-policy enforcement concern extracted from <c>AgentActor</c>:
/// evaluating model caps (#247), cost caps (#248), and execution-mode policy
/// (#249) against the per-turn effective metadata.
/// </summary>
/// <remarks>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates. This makes it safe to
/// register as a singleton and share across all <c>AgentActor</c> instances.
/// </remarks>
public class AgentUnitPolicyCoordinator(
    ILogger<AgentUnitPolicyCoordinator> logger) : IAgentUnitPolicyCoordinator
{
    /// <inheritdoc />
    public async Task<(AgentMetadata Effective, PolicyVerdict? Verdict)> ApplyUnitPoliciesAsync(
        string agentId,
        AgentMetadata effective,
        Func<string, string, CancellationToken, Task<PolicyDecision>> evaluateModel,
        Func<string, decimal, CancellationToken, Task<PolicyDecision>> evaluateCost,
        Func<string, AgentExecutionMode, CancellationToken, Task<ExecutionModeResolution>> resolveExecutionMode,
        CancellationToken cancellationToken = default)
    {
        // Model caps (#247): deny on block-list hit / whitelist miss. Null
        // model means the downstream dispatcher picks a default — no cap
        // applies at this seam.
        if (!string.IsNullOrWhiteSpace(effective.Model))
        {
            PolicyDecision modelDecision;
            try
            {
                modelDecision = await evaluateModel(agentId, effective.Model, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Unit policy enforcer threw evaluating model '{Model}' for agent {AgentId}; allowing to avoid losing the turn.",
                    effective.Model, agentId);
                modelDecision = PolicyDecision.Allowed;
            }

            if (!modelDecision.IsAllowed)
            {
                return (effective, new PolicyVerdict(
                    Dimension: "model",
                    DecisionTag: "BlockedByUnitModelPolicy",
                    Summary: modelDecision.Reason ?? $"model '{effective.Model}' denied",
                    Decision: modelDecision));
            }
        }

        // Cost caps (#248): zero projected cost — the enforcer still checks
        // whether the current rolling-window sum has already exceeded the cap.
        PolicyDecision costDecision;
        try
        {
            costDecision = await evaluateCost(agentId, 0m, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Unit policy enforcer threw evaluating cost for agent {AgentId}; allowing to avoid losing the turn.",
                agentId);
            costDecision = PolicyDecision.Allowed;
        }

        if (!costDecision.IsAllowed)
        {
            return (effective, new PolicyVerdict(
                Dimension: "cost",
                DecisionTag: "BlockedByUnitCostPolicy",
                Summary: costDecision.Reason ?? "cost cap exceeded",
                Decision: costDecision));
        }

        // Execution mode (#249): resolve — coercion by a forcing unit wins,
        // otherwise a non-matching allow-list denies.
        var requestedMode = effective.ExecutionMode ?? AgentExecutionMode.Auto;
        ExecutionModeResolution resolution;
        try
        {
            resolution = await resolveExecutionMode(agentId, requestedMode, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Unit policy enforcer threw evaluating execution mode for agent {AgentId}; allowing to avoid losing the turn.",
                agentId);
            resolution = ExecutionModeResolution.AllowAsIs(requestedMode);
        }

        if (!resolution.Decision.IsAllowed)
        {
            return (effective, new PolicyVerdict(
                Dimension: "executionMode",
                DecisionTag: "BlockedByUnitExecutionModePolicy",
                Summary: resolution.Decision.Reason ?? $"execution mode '{requestedMode}' denied",
                Decision: resolution.Decision));
        }

        if (resolution.Mode != requestedMode)
        {
            effective = effective with { ExecutionMode = resolution.Mode };
        }

        return (effective, null);
    }
}
