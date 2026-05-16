// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Lifecycle;

/// <summary>
/// Single source of truth for the artefact lifecycle state machine (#2364).
/// Both <c>UnitActor</c> and <c>AgentActor</c> consult this table on every
/// <c>TransitionAsync</c> call — replaces the in-line switch that previously
/// lived on <c>UnitActor</c> and never existed on <c>AgentActor</c>.
/// </summary>
public static class LifecycleTransitions
{
    /// <summary>
    /// Returns <c>true</c> when an artefact in state <paramref name="current"/>
    /// is allowed to transition to <paramref name="target"/>. Same arrows as the
    /// historical unit-only table (#944 / #939 / T-02): Draft is the create-time
    /// entry, every operational state can transition into Validating (creation +
    /// /revalidate), and Validating only exits to Stopped or Error via the
    /// validation workflow's terminal callback.
    /// </summary>
    /// <remarks>
    /// <c>Draft → Starting</c> is intentionally absent: artefacts must pass through
    /// <see cref="LifecycleStatus.Validating"/> before they can start (#939).
    /// </remarks>
    public static bool IsValidTransition(LifecycleStatus current, LifecycleStatus target) =>
        (current, target) switch
        {
            (LifecycleStatus.Draft, LifecycleStatus.Stopped) => true,
            (LifecycleStatus.Stopped, LifecycleStatus.Starting) => true,
            (LifecycleStatus.Starting, LifecycleStatus.Running) => true,
            (LifecycleStatus.Starting, LifecycleStatus.Error) => true,
            (LifecycleStatus.Running, LifecycleStatus.Stopping) => true,
            (LifecycleStatus.Stopping, LifecycleStatus.Stopped) => true,
            (LifecycleStatus.Stopping, LifecycleStatus.Error) => true,
            (LifecycleStatus.Error, LifecycleStatus.Stopped) => true,

            // Validation edges (#944 / T-02 / #939). Artefacts enter Validating
            // from Draft (on creation) or Stopped/Error (on /revalidate). The
            // ArtefactValidationWorkflow drives Validating → Stopped | Error
            // via CompleteValidationAsync on the actor.
            (LifecycleStatus.Draft, LifecycleStatus.Validating) => true,
            (LifecycleStatus.Validating, LifecycleStatus.Stopped) => true,
            (LifecycleStatus.Validating, LifecycleStatus.Error) => true,
            (LifecycleStatus.Error, LifecycleStatus.Validating) => true,
            (LifecycleStatus.Stopped, LifecycleStatus.Validating) => true,

            _ => false,
        };
}
