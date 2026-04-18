// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Cloning;

using System.Text.Json.Serialization;

/// <summary>
/// Persistent governance configuration for agent cloning (#416). Distinct
/// from the <see cref="CloningPolicy"/> enum, which describes the memory
/// shape of a single clone — this record persists the rules the platform
/// enforces on <em>every</em> clone of a given agent (or tenant-wide when
/// no agent-scoped record exists).
/// </summary>
/// <remarks>
/// <para>
/// Every slot is optional. A <c>null</c> slot means "no constraint at this
/// scope along this dimension" — resolution walks agent-scoped policy first,
/// then tenant-scoped, and a missing policy at both scopes is equivalent to
/// "unconstrained" (legacy behaviour: the lifecycle workflow validates only
/// what the request itself carries, preserving compatibility with clones
/// created before #416).
/// </para>
/// <para>
/// The policy is <em>applied at request time</em> by
/// <c>IAgentCloningPolicyEnforcer</c>, which composes the enforced input
/// and hands it to <c>ValidateCloneRequestActivity</c>. Operators edit the
/// record through <c>spring agent clone policy</c> and the unified
/// <c>/api/v1/agents/{id}/cloning-policy</c> / <c>/api/v1/tenant/cloning-policy</c>
/// endpoints; the portal half is tracked as a follow-up.
/// </para>
/// <para>
/// Unit-boundary interaction (PR #497): the clone's reach cannot exceed the
/// source agent's boundary. The enforcer surfaces this by refusing clone
/// requests whose attachment mode would place the clone outside the parent's
/// unit scope (e.g. <c>Detached</c> on an agent whose parent unit has an
/// <c>Opaque</c> boundary). The boundary itself stays authoritative —
/// <see cref="AgentCloningPolicy"/> only stores the rules the operator
/// declared, and the enforcer is responsible for cross-referencing them
/// against live unit state.
/// </para>
/// </remarks>
/// <param name="AllowedPolicies">
/// Optional allow-list of <see cref="CloningPolicy"/> values the enforcer
/// will accept. <c>null</c> means "any policy", matching the legacy
/// unconstrained default. An empty, non-null list denies every request.
/// </param>
/// <param name="AllowedAttachmentModes">
/// Optional allow-list of <see cref="AttachmentMode"/> values. <c>null</c>
/// means "any mode". The default portal surface requests
/// <see cref="AttachmentMode.Detached"/>; setting this slot to
/// <c>[Attached]</c> forces every clone to roll into an attached unit.
/// </param>
/// <param name="MaxClones">
/// Optional cap on the number of concurrent clones. Mirrors the
/// <c>max_clones</c> knob that already exists on the agent YAML — the
/// persistent policy is what the validation activity reads when a request
/// does not carry an inline <c>MaxClones</c>.
/// </param>
/// <param name="MaxDepth">
/// Optional recursion cap: how many layers of "clone of clone of ..." are
/// allowed. <c>0</c> explicitly disables recursive cloning; <c>null</c>
/// defers to the platform default of unbounded recursion (Phase 5 work —
/// the first concrete recursive-cloning surface lands in a follow-up).
/// The depth check walks <see cref="CloneIdentity.ParentAgentId"/> from the
/// source agent, so clones made before this PR still resolve to depth 0.
/// </param>
/// <param name="Budget">
/// Optional per-clone cost budget forwarded to the validation activity.
/// Existing contract: a zero-or-negative value is rejected up-front;
/// <c>null</c> means "no additional budget applied".
/// </param>
public record AgentCloningPolicy(
    IReadOnlyList<CloningPolicy>? AllowedPolicies = null,
    IReadOnlyList<AttachmentMode>? AllowedAttachmentModes = null,
    int? MaxClones = null,
    int? MaxDepth = null,
    decimal? Budget = null)
{
    /// <summary>
    /// Returns an empty policy — no constraints in any dimension.
    /// Equivalent to "no persistent policy for this scope".
    /// </summary>
    public static AgentCloningPolicy Empty { get; } = new();

    /// <summary>
    /// Returns <c>true</c> when every slot is <c>null</c> — the policy
    /// carries no constraints. Repositories may treat an all-null policy
    /// as a row deletion so the table reflects scopes that actually have a
    /// policy.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => AllowedPolicies is null
        && AllowedAttachmentModes is null
        && MaxClones is null
        && MaxDepth is null
        && Budget is null;
}