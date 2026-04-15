// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

using Cvoya.Spring.Core.Agents;

/// <summary>
/// Return shape of <see cref="IUnitPolicyEnforcer.ResolveExecutionModeAsync"/>.
/// Combines a <see cref="PolicyDecision"/> (were we allowed to dispatch at
/// all?) with the actually-effective mode after unit-level coercion has been
/// applied. Callers route on <c>Decision.IsAllowed</c> first — a denied
/// decision means the dispatch must not proceed at the supplied mode, while
/// <see cref="Mode"/> reflects the mode a forcing unit demanded (or the
/// input mode if the input was already legal).
/// </summary>
/// <param name="Decision">
/// The underlying <see cref="PolicyDecision"/>. When
/// <c>Decision.IsAllowed</c> is <c>true</c>, the caller should dispatch under
/// <see cref="Mode"/>. When <c>false</c>, the call must be skipped — the
/// denying unit and reason are carried on the decision.
/// </param>
/// <param name="Mode">
/// The effective execution mode. Equal to the caller's input mode when no
/// unit forces a different value; equal to the forcing unit's
/// <see cref="ExecutionModePolicy.Forced"/> value otherwise.
/// </param>
public readonly record struct ExecutionModeResolution(
    PolicyDecision Decision,
    AgentExecutionMode Mode)
{
    /// <summary>
    /// Convenience factory: the input mode is legal and no unit coerces it.
    /// </summary>
    public static ExecutionModeResolution AllowAsIs(AgentExecutionMode mode) =>
        new(PolicyDecision.Allowed, mode);

    /// <summary>
    /// Convenience factory: a unit forces the mode. The decision is still
    /// <c>Allowed</c> (the caller may dispatch), but under the forced mode.
    /// </summary>
    public static ExecutionModeResolution Coerced(AgentExecutionMode mode) =>
        new(PolicyDecision.Allowed, mode);
}