// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Lifecycle;

using System.Text.Json;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IArtefactValidationCoordinator"/>.
/// Owns the validation-scheduling concern extracted from the actors (#2364):
/// scheduling the <c>ArtefactValidationWorkflow</c>, persisting run ids and
/// error payloads through <see cref="IArtefactValidationTracker"/>, and
/// handling the workflow's terminal callback via
/// <see cref="CompleteValidationAsync"/>. Routes per-<see cref="ArtefactKind"/>
/// so the same coordinator instance serves both <c>UnitActor</c> and
/// <c>AgentActor</c>.
/// </summary>
/// <remarks>
/// The coordinator is stateless with respect to any individual artefact — it
/// operates entirely through the <c>persistTransition</c> and
/// <c>getCurrentStatus</c> delegates passed per call, and through the
/// injected <see cref="IArtefactValidationWorkflowScheduler"/> /
/// <see cref="IArtefactValidationTracker"/> singletons. This makes it safe to
/// register as a singleton and share across all actor instances.
/// </remarks>
public class ArtefactValidationCoordinator(
    IArtefactValidationWorkflowScheduler? scheduler,
    IArtefactValidationTracker? tracker,
    ILogger<ArtefactValidationCoordinator> logger) : IArtefactValidationCoordinator
{
    /// <inheritdoc />
    public async Task<TransitionResult?> TryStartWorkflowAsync(
        ArtefactKind kind,
        string artefactActorId,
        Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>> persistTransition,
        CancellationToken cancellationToken = default)
    {
        if (scheduler is null)
        {
            logger.LogDebug(
                "{Kind} {ActorId} transitioned to Validating without a validation workflow scheduler; no probe will run.",
                kind, artefactActorId);
            return null;
        }

        try
        {
            var schedule = await scheduler.ScheduleAsync(kind, artefactActorId, cancellationToken);

            if (tracker is not null)
            {
                await tracker.BeginRunAsync(kind, artefactActorId, schedule.WorkflowInstanceId, cancellationToken);
            }

            logger.LogInformation(
                "{Kind} {ActorId} scheduled validation workflow {WorkflowInstanceId} for {ArtefactName}.",
                kind, artefactActorId, schedule.WorkflowInstanceId, schedule.ArtefactName);

            return null;
        }
        catch (ArtefactValidationSchedulingException ex)
        {
            // #1144: the scheduler determined — without running any
            // in-container probes — that the artefact cannot validate
            // (e.g. no image configured). Persist the *structured* failure
            // and flip straight to Error so the wizard can render
            // field-specific recovery copy ("Image is required") rather
            // than the generic ScheduleFailed catch-all.
            logger.LogWarning(
                "{Kind} {ActorId} validation rejected by scheduler ({Code}): {Message}",
                kind, artefactActorId, ex.Error.Code, ex.Error.Message);

            return await PersistSchedulerFailureAsync(kind, artefactActorId, ex.Error, persistTransition, cancellationToken);
        }
        catch (Exception ex)
        {
            // #1136: a scheduler-side failure used to leave the artefact
            // permanently in Validating with no LastValidationRunId. Treat
            // it as a validation failure and tombstone the artefact into
            // Error with a structured ScheduleFailed payload so the
            // standard recovery paths (delete without force, revalidate
            // from Error) work without operator knowledge of the force
            // escape hatch.
            logger.LogError(
                ex,
                "{Kind} {ActorId} failed to schedule validation workflow; flipping to Error.",
                kind, artefactActorId);

            var failure = new ArtefactValidationError(
                Step: ArtefactValidationStep.SchedulingWorkflow,
                Code: ArtefactValidationCodes.ScheduleFailed,
                Message: $"Failed to schedule validation workflow: {ex.GetType().Name}: {ex.Message}",
                Details: null);
            return await PersistSchedulerFailureAsync(kind, artefactActorId, failure, persistTransition, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<TransitionResult> CompleteValidationAsync(
        ArtefactKind kind,
        string artefactActorId,
        ArtefactValidationCompletion completion,
        Func<CancellationToken, Task<LifecycleStatus>> getCurrentStatus,
        Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>> persistTransition,
        CancellationToken cancellationToken = default)
    {
        var current = await getCurrentStatus(cancellationToken);

        // Terminal-status guard: if we're already Stopped / Error (e.g. a
        // second workflow superseded this one), silently drop the callback
        // rather than overwriting current state.
        if (current == LifecycleStatus.Stopped || current == LifecycleStatus.Error)
        {
            logger.LogInformation(
                "{Kind} {ActorId} ignoring validation completion from workflow {WorkflowInstanceId}: status is already terminal ({Status}).",
                kind, artefactActorId, completion.WorkflowInstanceId, current);
            return new TransitionResult(
                false, current,
                $"validation completion ignored: artefact already {current}");
        }

        // Stale-run guard: compare against the persisted LastValidationRunId.
        // For Agent kind the tracker no-ops the read (returns null) — accepting
        // every completion as fresh is the documented v0.1 limitation.
        if (tracker is not null)
        {
            var currentRunId = await tracker.GetLastValidationRunIdAsync(kind, artefactActorId, cancellationToken);
            if (currentRunId is not null
                && !string.Equals(currentRunId, completion.WorkflowInstanceId, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "{Kind} {ActorId} ignoring validation completion from workflow {WorkflowInstanceId}: stale run (current {CurrentRunId}).",
                    kind, artefactActorId, completion.WorkflowInstanceId, currentRunId);
                return new TransitionResult(
                    false, current,
                    "validation completion ignored: stale workflow run id");
            }
        }

        // A completion only makes sense from Validating; any other non-
        // terminal state means a transition was racing.
        if (current != LifecycleStatus.Validating)
        {
            logger.LogWarning(
                "{Kind} {ActorId} received validation completion but current status is {Status}; expected Validating.",
                kind, artefactActorId, current);
            return new TransitionResult(
                false, current,
                $"validation completion ignored: status is {current}, expected Validating");
        }

        if (completion.Success)
        {
            // Clear any prior failure payload first.
            if (tracker is not null)
            {
                await tracker.SetFailureAsync(kind, artefactActorId, null, cancellationToken);
            }

            return await persistTransition(LifecycleStatus.Validating, LifecycleStatus.Stopped, null, cancellationToken);
        }

        // Failure: serialize the payload and persist before the transition
        // write so any downstream reader of Error status also sees the
        // failure blob on the same row.
        if (tracker is not null)
        {
            var payload = completion.Failure is null
                ? null
                : JsonSerializer.Serialize(completion.Failure);
            await tracker.SetFailureAsync(kind, artefactActorId, payload, cancellationToken);
        }

        // #1665: forward the structured failure to PersistTransitionAsync so
        // the StateChanged activity event can elevate its severity and embed
        // the validation code/message in the activity feed.
        return await persistTransition(
            LifecycleStatus.Validating, LifecycleStatus.Error, completion.Failure, cancellationToken);
    }

    /// <summary>
    /// Persists a scheduler-side failure: writes the structured error blob
    /// and transitions the artefact out of <see cref="LifecycleStatus.Validating"/>
    /// into <see cref="LifecycleStatus.Error"/> via the actor's
    /// <c>persistTransition</c> delegate. Best-effort: a tracker-write
    /// failure here does not block the recovery transition.
    /// </summary>
    private async Task<TransitionResult> PersistSchedulerFailureAsync(
        ArtefactKind kind,
        string artefactActorId,
        ArtefactValidationError error,
        Func<LifecycleStatus, LifecycleStatus, ArtefactValidationError?, CancellationToken, Task<TransitionResult>> persistTransition,
        CancellationToken cancellationToken)
    {
        if (tracker is not null)
        {
            try
            {
                var payload = JsonSerializer.Serialize(error);
                await tracker.SetFailureAsync(kind, artefactActorId, payload, cancellationToken);
            }
            catch (Exception persistEx)
            {
                logger.LogWarning(
                    persistEx,
                    "{Kind} {ActorId}: failed to persist scheduler-failure payload ({Code}) before Error transition.",
                    kind, artefactActorId, error.Code);
            }
        }

        // #1665: forward the structured failure to PersistTransitionAsync so
        // the StateChanged activity event can elevate its severity and embed
        // the validation code/message in the activity feed.
        return await persistTransition(LifecycleStatus.Validating, LifecycleStatus.Error, error, cancellationToken);
    }
}
