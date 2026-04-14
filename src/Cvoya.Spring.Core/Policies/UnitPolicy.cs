// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

/// <summary>
/// Unit-level governance policy. A unit, as a trust / governance boundary,
/// can constrain the behaviour of agents within it along multiple dimensions.
/// Each dimension is represented by an optional sub-record: a <c>null</c>
/// value means "no constraint along this dimension" — agents see their own
/// (or a higher-level) policy in that slot.
/// </summary>
/// <remarks>
/// <para>
/// This is the framework shape introduced by #162. The first concrete
/// dimension wired up through <see cref="IUnitPolicyEnforcer"/> is
/// <see cref="Skill"/> (#163). Slots reserved for future dimensions are
/// present as <c>null</c> placeholders on this record and documented as TODO
/// — adding a dimension means adding the sub-record type and extending the
/// enforcer, not reshaping the storage row.
/// </para>
/// <para>
/// Interaction with per-membership overrides (C2b-1 / #160): unit policy is
/// unit-global and applies to every member agent. Per-membership overrides
/// are agent-specific within a unit. The unit policy takes precedence —
/// if the unit blocks a skill, no individual agent can use it regardless of
/// their own declaration or per-membership override.
/// </para>
/// </remarks>
/// <param name="Skill">
/// Optional <see cref="SkillPolicy"/> constraining which tools agents in this
/// unit may invoke. <c>null</c> means no skill constraint applies at the unit
/// level.
/// </param>
// Future slots (not yet implemented — see issue backlog):
//   ModelPolicy? Model
//   CostPolicy?  Cost
//   ExecutionModePolicy? ExecutionMode
//   InitiativePolicy?    Initiative
// Each additional slot is additive and backwards-compatible because every
// sub-record is nullable.
public record UnitPolicy(SkillPolicy? Skill = null)
{
    /// <summary>
    /// Returns an empty policy — no constraints in any dimension.
    /// Equivalent to "unit does not restrict member agents".
    /// </summary>
    public static UnitPolicy Empty { get; } = new();
}