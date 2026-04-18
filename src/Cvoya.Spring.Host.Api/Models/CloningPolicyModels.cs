// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.Cloning;

/// <summary>
/// Wire shape for the persistent cloning policy (#416). Mirrors
/// <see cref="AgentCloningPolicy"/> with explicit plain scalars so the
/// Kiota-generated client sees a simple, stable contract (no computed
/// <c>IsEmpty</c> leaking through).
/// </summary>
/// <param name="AllowedPolicies">
/// Optional allow-list of <see cref="CloningPolicy"/> values.
/// </param>
/// <param name="AllowedAttachmentModes">
/// Optional allow-list of <see cref="AttachmentMode"/> values.
/// </param>
/// <param name="MaxClones">
/// Optional cap on concurrent clones for this scope.
/// </param>
/// <param name="MaxDepth">
/// Optional recursion cap — <c>0</c> disables recursive cloning entirely;
/// <c>null</c> defers to the platform default.
/// </param>
/// <param name="Budget">
/// Optional per-clone cost budget the validation activity forwards to the
/// lifecycle workflow.
/// </param>
public record AgentCloningPolicyResponse(
    IReadOnlyList<CloningPolicy>? AllowedPolicies = null,
    IReadOnlyList<AttachmentMode>? AllowedAttachmentModes = null,
    int? MaxClones = null,
    int? MaxDepth = null,
    decimal? Budget = null)
{
    /// <summary>Projects a core <see cref="AgentCloningPolicy"/> onto the wire shape.</summary>
    public static AgentCloningPolicyResponse From(AgentCloningPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new AgentCloningPolicyResponse(
            policy.AllowedPolicies,
            policy.AllowedAttachmentModes,
            policy.MaxClones,
            policy.MaxDepth,
            policy.Budget);
    }

    /// <summary>Projects the wire shape back to the core record.</summary>
    public AgentCloningPolicy ToCore() => new(
        AllowedPolicies,
        AllowedAttachmentModes,
        MaxClones,
        MaxDepth,
        Budget);
}