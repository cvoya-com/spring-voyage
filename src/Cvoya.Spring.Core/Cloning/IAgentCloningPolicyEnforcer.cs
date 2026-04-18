// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Cloning;

/// <summary>
/// Narrow, DI-swappable enforcement seam for the persistent cloning policy
/// (#416). Called by the clone-create endpoint before it schedules the
/// lifecycle workflow so operator-visible errors surface as 400s rather
/// than silent workflow failures.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order: agent-scoped policy first, tenant-scoped second. A
/// deny at the agent scope short-circuits — the tenant policy is not
/// consulted because agent-level policy is the tighter surface. Numeric
/// caps (<c>MaxClones</c>, <c>MaxDepth</c>, <c>Budget</c>) use the
/// minimum of any non-null value across the scopes so a tenant ceiling
/// can't be relaxed by an agent-scoped override.
/// </para>
/// <para>
/// Boundary honouring (PR #497): the enforcer refuses requests whose
/// <see cref="AttachmentMode"/> would take a clone outside the parent
/// agent's unit boundary. Cross-referencing boundary state happens here
/// rather than on the raw <c>AgentCloningPolicy</c> record so the record
/// stays a plain value object; the enforcer is the single place that
/// knows how to compose stored rules with live unit state.
/// </para>
/// </remarks>
public interface IAgentCloningPolicyEnforcer
{
    /// <summary>
    /// Evaluates a clone request against the persistent policies stored
    /// for the source agent and the current tenant, plus any live
    /// boundary constraints on the parent's unit.
    /// </summary>
    /// <param name="sourceAgentId">
    /// Identifier of the agent being cloned.
    /// </param>
    /// <param name="requestedPolicy">
    /// The memory-shape enum the caller asked for.
    /// </param>
    /// <param name="requestedAttachmentMode">
    /// The attachment mode the caller asked for.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The decision — carrying resolved cap values the validation activity
    /// should forward to the lifecycle workflow.
    /// </returns>
    Task<CloningPolicyDecision> EvaluateAsync(
        string sourceAgentId,
        CloningPolicy requestedPolicy,
        AttachmentMode requestedAttachmentMode,
        CancellationToken cancellationToken = default);
}