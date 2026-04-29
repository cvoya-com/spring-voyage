// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Seam that encapsulates the validation-scheduling concern extracted from
/// <c>UnitActor</c>: receiving the trigger to enter
/// <see cref="UnitStatus.Validating"/>, scheduling the
/// <c>UnitValidationWorkflow</c> via
/// <see cref="IUnitValidationWorkflowScheduler"/>, persisting the run id
/// through <see cref="IUnitValidationTracker"/>, and driving the terminal
/// callbacks when the workflow completes.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host can
/// substitute a tenant-aware coordinator (e.g. one that routes workflow
/// scheduling to a per-tenant Dapr app id and layers audit logging on
/// every state write) without touching the actor. Per the platform's
/// "interface-first + TryAdd*" rule, production DI registers the default
/// implementation with <c>TryAddSingleton</c> so the private repo's
/// registration takes precedence when present.
/// </para>
/// <para>
/// The coordinator does not hold a reference to the actor. Instead, both
/// methods receive a <see cref="PersistTransitionAsync"/> delegate so the
/// actor can inject its own state-write + activity-event implementation
/// without the coordinator depending on Dapr actor types.
/// </para>
/// </remarks>
public interface IUnitValidationCoordinator
{
    /// <summary>
    /// Called by the actor immediately after it has successfully persisted
    /// the transition into <see cref="UnitStatus.Validating"/>. Schedules
    /// the <c>UnitValidationWorkflow</c>, persists the returned instance id,
    /// and returns:
    /// <list type="bullet">
    ///   <item><description>
    ///     <see langword="null"/> on the happy path — the caller's existing
    ///     <c>Draft|Stopped|Error→Validating</c> transition stands.
    ///   </description></item>
    ///   <item><description>
    ///     A non-null <see cref="TransitionResult"/> when the scheduler threw
    ///     and the coordinator recovered by calling
    ///     <paramref name="persistTransition"/> to flip the unit to
    ///     <see cref="UnitStatus.Error"/> — the caller should return this
    ///     result so observers see the final state without a separate status
    ///     read (#1136).
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <param name="unitActorId">The unit's Dapr actor id.</param>
    /// <param name="persistTransition">
    /// Delegate that writes the status to actor state and emits the
    /// <c>StateChanged</c> activity event. Called by the coordinator when
    /// scheduler failure forces a recovery transition into
    /// <see cref="UnitStatus.Error"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the schedule.</param>
    Task<TransitionResult?> TryStartWorkflowAsync(
        string unitActorId,
        Func<UnitStatus, UnitStatus, CancellationToken, Task<TransitionResult>> persistTransition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles the terminal callback posted by the
    /// <c>UnitValidationWorkflow</c>. Applies the stale-run and
    /// terminal-status guards, persists the failure payload (on failure),
    /// and drives the appropriate
    /// <see cref="UnitStatus.Validating"/>→<see cref="UnitStatus.Stopped"/> or
    /// <see cref="UnitStatus.Validating"/>→<see cref="UnitStatus.Error"/>
    /// transition through <paramref name="persistTransition"/>.
    /// </summary>
    /// <param name="unitActorId">The unit's Dapr actor id.</param>
    /// <param name="completion">The workflow's completion payload.</param>
    /// <param name="getCurrentStatus">
    /// Delegate that reads the current <see cref="UnitStatus"/> from actor
    /// state. The coordinator calls this to evaluate the stale-run and
    /// terminal-status guards.
    /// </param>
    /// <param name="persistTransition">
    /// Delegate that writes the status to actor state and emits the
    /// <c>StateChanged</c> activity event.
    /// </param>
    /// <param name="cancellationToken">Cancels the completion handling.</param>
    Task<TransitionResult> CompleteValidationAsync(
        string unitActorId,
        UnitValidationCompletion completion,
        Func<CancellationToken, Task<UnitStatus>> getCurrentStatus,
        Func<UnitStatus, UnitStatus, CancellationToken, Task<TransitionResult>> persistTransition,
        CancellationToken cancellationToken = default);
}