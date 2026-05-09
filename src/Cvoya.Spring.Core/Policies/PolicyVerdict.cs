// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

/// <summary>
/// Carries a unit-policy denial across the agent-dispatch plumbing.
/// Returned by <see cref="IAgentUnitPolicyCoordinator.ApplyUnitPoliciesAsync"/>
/// and consumed by <c>AgentActor.HandleDomainMessageAsync</c> to emit a
/// structured <c>DecisionMade</c> activity event without threading raw
/// <see cref="PolicyDecision"/> values into every caller.
/// </summary>
/// <param name="Dimension">
/// The policy dimension that triggered the denial (e.g. <c>"model"</c>,
/// <c>"cost"</c>, <c>"executionMode"</c>).
/// </param>
/// <param name="DecisionTag">
/// A short machine-readable tag that identifies the denial category (e.g.
/// <c>"BlockedByUnitModelPolicy"</c>). Surfaced on the activity event's
/// <c>details.decision</c> field for structured log queries.
/// </param>
/// <param name="Summary">
/// A human-readable summary of the denial (e.g. the model name that was
/// blocked, or the cost cap that was exceeded). Surfaced on the activity
/// event's <c>summary</c> field.
/// </param>
/// <param name="Decision">
/// The raw <see cref="PolicyDecision"/> returned by the enforcer, carrying
/// the denial reason and the id of the first denying unit.
/// </param>
public sealed record PolicyVerdict(
    string Dimension,
    string DecisionTag,
    string Summary,
    PolicyDecision Decision);
