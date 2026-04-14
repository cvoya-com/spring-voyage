// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.Policies;

/// <summary>
/// Wire-level representation of a <see cref="UnitPolicy"/> surfaced through
/// the unified <c>/api/v1/units/{id}/policy</c> endpoint. Thin wrapper kept
/// separate from the core record so OpenAPI surfaces a stable model name
/// ("UnitPolicyResponse") independent of any internal refactors to the
/// core policy shape.
/// </summary>
/// <param name="Skill">Optional skill policy; <c>null</c> means no skill constraint.</param>
public record UnitPolicyResponse(SkillPolicy? Skill)
{
    /// <summary>Lifts a core <see cref="UnitPolicy"/> into the response shape.</summary>
    public static UnitPolicyResponse From(UnitPolicy policy) => new(policy.Skill);

    /// <summary>Projects this response back into the core record.</summary>
    public UnitPolicy ToCore() => new(Skill);
}