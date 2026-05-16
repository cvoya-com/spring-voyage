// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Lifecycle;

using Cvoya.Spring.Core.Artefacts;

/// <summary>
/// Seam that encapsulates the validation-scheduling concern extracted from
/// <c>UnitActor</c> and shared with <c>AgentActor</c> (#2364): receiving the
/// trigger to enter <see cref="LifecycleStatus.Validating"/>, scheduling the
/// <c>ArtefactValidationWorkflow</c> via
/// <see cref="IArtefactValidationWorkflowScheduler"/>, persisting the run id
/// through <see cref="IArtefactValidationTracker"/>, and driving the terminal
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
/// methods receive a <c>persistTransition</c> delegate so the actor can
/// inject its own state-write + activity-event implementation without the
/// coordinator depending on Dapr actor types. The
/// <see cref="ArtefactKind"/> parameter routes the workflow input + tracker
/// write to the right per-kind store.
/// </para>
/// </remarks>
public interface IArtefactValidationCoordinator
{
    /// <summary>
    /// Called by the actor immediately after it has successfully persisted
    /// the transition into <see cref="LifecycleStatus.Validating"/>. Schedules
    /// the <c>ArtefactValidationWorkflow</c>, persists the returned instance id,
    /// and returns:
    /// <list type="bullet">
    ///   <item><description>
    ///     <see langword="null"/> on the happy path — the caller's existing
    ///     <c>Draft|Stopped|Error→Validating</c> transition stands.
    ///   </description></item>
    ///   <item><description>
    ///     A non-null <see cref="TransitionResult"/> when the scheduler threw
    ///     and the coordinator recovered by calling
    ///     <paramref name="persistTransition"/> to flip the artefact to
    ///     <see cref="LifecycleStatus.Error"/> — the caller should return this
    ///     result so observers see the final state without a separate status
    ///     read (#1136).
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <param name="kind">Whether the artefact is a Unit or an Agent.</param>
    /// <param name="artefactActorId">The artefact's Dapr actor id.</param>
    /// <param name="persistTransition">
    /// Delegate that writes the status to actor state and emits the
    /// <c>StateChanged</c> activity event. Called by the coordinator when
    /// scheduler failure forces a recovery transition into
    /// <see cref="LifecycleStatus.Error"/>. The optional
    /// <see cref="ArtefactValidationError"/> argument carries the structured
    /// failure context (#1665) so the activity event can elevate severity
    /// and inject the validation <c>code</c>/<c>message</c> into
    /// <c>summary</c> + <c>details</c>; passed as <c>null</c> for non-failure
    /// transitions.
    /// </param>
    /// <param name="cancellationToken">Cancels the schedule.</param>
    Task<TransitionResult?> TryStartWorkflowAsync(
        ArtefactKind kind,
        string artefactActorId,
        Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>> persistTransition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles the terminal callback posted by the
    /// <c>ArtefactValidationWorkflow</c>. Applies the stale-run and
    /// terminal-status guards, persists the failure payload (on failure),
    /// and drives the appropriate
    /// <see cref="LifecycleStatus.Validating"/>→<see cref="LifecycleStatus.Stopped"/> or
    /// <see cref="LifecycleStatus.Validating"/>→<see cref="LifecycleStatus.Error"/>
    /// transition through <paramref name="persistTransition"/>.
    /// </summary>
    /// <param name="kind">Whether the artefact is a Unit or an Agent — drives the per-kind tracker store.</param>
    /// <param name="artefactActorId">The artefact's Dapr actor id.</param>
    /// <param name="completion">The workflow's completion payload.</param>
    /// <param name="getCurrentStatus">
    /// Delegate that reads the current <see cref="LifecycleStatus"/> from actor
    /// state. The coordinator calls this to evaluate the stale-run and
    /// terminal-status guards.
    /// </param>
    /// <param name="persistTransition">
    /// Delegate that writes the status to actor state and emits the
    /// <c>StateChanged</c> activity event. The optional
    /// <see cref="ArtefactValidationError"/> argument carries the structured
    /// failure context (#1665) so the activity event can elevate severity
    /// and inject the validation <c>code</c>/<c>message</c> into
    /// <c>summary</c> + <c>details</c>; passed as <c>null</c> on the
    /// success path.
    /// </param>
    /// <param name="cancellationToken">Cancels the completion handling.</param>
    Task<TransitionResult> CompleteValidationAsync(
        ArtefactKind kind,
        string artefactActorId,
        ArtefactValidationCompletion completion,
        Func<CancellationToken, Task<LifecycleStatus>> getCurrentStatus,
        Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>> persistTransition,
        CancellationToken cancellationToken = default);
}
