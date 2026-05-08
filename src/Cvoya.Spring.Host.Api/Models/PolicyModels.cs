// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Policies;

/// <summary>
/// Wire-level representation of a <see cref="UnitPolicy"/> surfaced through
/// the unified <c>/api/v1/units/{id}/policy</c> endpoint. Thin wrapper kept
/// separate from the core record so OpenAPI surfaces a stable model name
/// ("UnitPolicyResponse") independent of any internal refactors to the
/// core policy shape.
/// </summary>
/// <param name="Skill">Optional skill policy; <c>null</c> means no skill constraint.</param>
/// <param name="Model">Optional model policy (#247); <c>null</c> means no model constraint.</param>
/// <param name="Cost">Optional cost policy (#248); <c>null</c> means no cost cap.</param>
/// <param name="ExecutionMode">Optional execution-mode policy (#249); <c>null</c> means no mode constraint.</param>
/// <param name="Initiative">
/// Optional unit-level initiative policy (#250); <c>null</c> means the unit
/// does not overlay the agent-level initiative policy with a deny filter.
/// </param>
public record UnitPolicyResponse(
    SkillPolicy? Skill = null,
    ModelPolicy? Model = null,
    CostPolicy? Cost = null,
    ExecutionModePolicy? ExecutionMode = null,
    InitiativePolicy? Initiative = null)
{
    /// <summary>Lifts a core <see cref="UnitPolicy"/> into the response shape.</summary>
    public static UnitPolicyResponse From(UnitPolicy policy) =>
        new(policy.Skill, policy.Model, policy.Cost, policy.ExecutionMode, policy.Initiative);

    /// <summary>Projects this response back into the core record.</summary>
    public UnitPolicy ToCore() => new(Skill, Model, Cost, ExecutionMode, Initiative);
}