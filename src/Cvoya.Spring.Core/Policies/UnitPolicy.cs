// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

using Cvoya.Spring.Core.Initiative;

/// <summary>
/// Unit-level governance policy. A unit, as a trust / governance boundary,
/// can constrain the behaviour of agents within it along multiple dimensions.
/// Each dimension is represented by an optional sub-record: a <c>null</c>
/// value means "no constraint along this dimension" — agents see their own
/// (or a higher-level) policy in that slot.
/// </summary>
/// <remarks>
/// <para>
/// This is the framework shape introduced by #162. C3 (#163) wired the first
/// concrete dimension, <see cref="Skill"/>. Wave 7 C6 (#247 / #248 / #249 /
/// #250) adds four more: <see cref="Model"/>, <see cref="Cost"/>,
/// <see cref="ExecutionMode"/>, and <see cref="Initiative"/>. Each dimension
/// has its own sub-record and its own evaluator on
/// <see cref="IUnitPolicyEnforcer"/>; adding a dimension is additive and does
/// not require reshaping persisted rows because every slot is nullable.
/// </para>
/// <para>
/// Interaction with per-membership overrides (C2b-1 / #160): unit policy is
/// unit-global and applies to every member agent. Per-membership overrides
/// are agent-specific within a unit. The unit policy takes precedence —
/// if the unit blocks a skill, pins an execution mode, or denies a model, no
/// individual agent can escape the constraint via per-membership override or
/// their own declaration.
/// </para>
/// </remarks>
/// <param name="Skill">
/// Optional <see cref="SkillPolicy"/> constraining which tools agents in this
/// unit may invoke. <c>null</c> means no skill constraint applies at the unit
/// level.
/// </param>
/// <param name="Model">
/// Optional <see cref="ModelPolicy"/> constraining which LLM models agents in
/// this unit may run under (#247). <c>null</c> means no model constraint.
/// </param>
/// <param name="Cost">
/// Optional <see cref="CostPolicy"/> setting per-invocation / per-hour /
/// per-day cost ceilings for agents in this unit (#248). <c>null</c> means no
/// unit-level cost cap.
/// </param>
/// <param name="ExecutionMode">
/// Optional <see cref="ExecutionModePolicy"/> restricting or pinning the
/// execution mode for agents in this unit (#249). <c>null</c> means no unit-
/// level execution-mode constraint — per-membership / agent-global values win
/// unchanged.
/// </param>
/// <param name="Initiative">
/// Optional unit-level <see cref="InitiativePolicy"/> DENY-overlay on the
/// per-agent initiative policy (#250). <c>null</c> means no unit-level
/// initiative constraint — the agent's own policy (stored via
/// <see cref="IAgentPolicyStore"/>) is authoritative.
/// </param>
public record UnitPolicy(
    SkillPolicy? Skill = null,
    ModelPolicy? Model = null,
    CostPolicy? Cost = null,
    ExecutionModePolicy? ExecutionMode = null,
    InitiativePolicy? Initiative = null)
{
    /// <summary>
    /// Returns an empty policy — no constraints in any dimension.
    /// Equivalent to "unit does not restrict member agents".
    /// </summary>
    public static UnitPolicy Empty { get; } = new();

    /// <summary>
    /// Returns <c>true</c> when every dimension is <c>null</c> — the policy
    /// carries no constraints. Repositories may treat an all-null policy as a
    /// row deletion.
    /// </summary>
    public bool IsEmpty => Skill is null
        && Model is null
        && Cost is null
        && ExecutionMode is null
        && Initiative is null;
}