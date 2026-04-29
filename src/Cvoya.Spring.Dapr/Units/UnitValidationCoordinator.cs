// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using System.Text.Json;

using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IUnitValidationCoordinator"/>.
/// Owns the validation-scheduling concern extracted from
/// <c>UnitActor</c>: scheduling the <c>UnitValidationWorkflow</c>,
/// persisting run ids and error payloads through
/// <see cref="IUnitValidationTracker"/>, and handling the workflow's
/// terminal callback via <see cref="CompleteValidationAsync"/>.
/// </summary>
/// <remarks>
/// The coordinator is stateless with respect to any individual unit — it
/// operates entirely through the <paramref name="persistTransition"/> and
/// <paramref name="getCurrentStatus"/> delegates passed per call, and
/// through the injected <see cref="IUnitValidationWorkflowScheduler"/> /
/// <see cref="IUnitValidationTracker"/> singletons. This makes it safe to
/// register as a singleton and share across all <c>UnitActor</c> instances.
/// </remarks>
public class UnitValidationCoordinator(
    IUnitValidationWorkflowScheduler? scheduler,
    IUnitValidationTracker? tracker,
    ILogger<UnitValidationCoordinator> logger) : IUnitValidationCoordinator
{
    /// <inheritdoc />
    public async Task<TransitionResult?> TryStartWorkflowAsync(
        string unitActorId,
        Func<UnitStatus, UnitStatus, CancellationToken, Task<TransitionResult>> persistTransition,
        CancellationToken cancellationToken = default)
    {
        if (scheduler is null)
        {
            logger.LogDebug(
                "Unit {ActorId} transitioned to Validating without a validation workflow scheduler; no probe will run.",
                unitActorId);
            return null;
        }

        try
        {
            var schedule = await scheduler.ScheduleAsync(unitActorId, cancellationToken);

            if (tracker is not null)
            {
                await tracker.BeginRunAsync(unitActorId, schedule.WorkflowInstanceId, cancellationToken);
            }

            logger.LogInformation(
                "Unit {ActorId} scheduled validation workflow {WorkflowInstanceId} for unit {UnitName}.",
                unitActorId, schedule.WorkflowInstanceId, schedule.UnitName);

            return null;
        }
        catch (UnitValidationSchedulingException ex)
        {
            // #1144: the scheduler determined — without running any
            // in-container probes — that the unit cannot validate (e.g. no
            // image configured). Persist the *structured* failure and flip
            // straight to Error so the wizard can render field-specific
            // recovery copy ("Image is required") rather than the generic
            // ScheduleFailed catch-all.
            logger.LogWarning(
                "Unit {ActorId} validation rejected by scheduler ({Code}): {Message}",
                unitActorId, ex.Error.Code, ex.Error.Message);

            return await PersistSchedulerFailureAsync(unitActorId, ex.Error, persistTransition, cancellationToken);
        }
        catch (Exception ex)
        {
            // #1136: a scheduler-side failure used to leave the unit
            // permanently in Validating with no LastValidationRunId.
            // Treat it as a validation failure and tombstone the unit
            // into Error with a structured ScheduleFailed payload so the
            // standard recovery paths (delete without force, revalidate
            // from Error) work without operator knowledge of the force
            // escape hatch.
            logger.LogError(
                ex,
                "Unit {ActorId} failed to schedule validation workflow; flipping to Error.",
                unitActorId);

            var failure = new UnitValidationError(
                Step: UnitValidationStep.SchedulingWorkflow,
                Code: UnitValidationCodes.ScheduleFailed,
                Message: $"Failed to schedule validation workflow: {ex.GetType().Name}: {ex.Message}",
                Details: null);
            return await PersistSchedulerFailureAsync(unitActorId, failure, persistTransition, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<TransitionResult> CompleteValidationAsync(
        string unitActorId,
        UnitValidationCompletion completion,
        Func<CancellationToken, Task<UnitStatus>> getCurrentStatus,
        Func<UnitStatus, UnitStatus, CancellationToken, Task<TransitionResult>> persistTransition,
        CancellationToken cancellationToken = default)
    {
        var current = await getCurrentStatus(cancellationToken);

        // Terminal-status guard: if we're already Stopped / Error (e.g. a
        // second workflow superseded this one), silently drop the callback
        // rather than overwriting current state.
        if (current == UnitStatus.Stopped || current == UnitStatus.Error)
        {
            logger.LogInformation(
                "Unit {ActorId} ignoring validation completion from workflow {WorkflowInstanceId}: status is already terminal ({Status}).",
                unitActorId, completion.WorkflowInstanceId, current);
            return new TransitionResult(
                false, current,
                $"validation completion ignored: unit already {current}");
        }

        // Stale-run guard: compare against the persisted LastValidationRunId.
        if (tracker is not null)
        {
            var currentRunId = await tracker.GetLastValidationRunIdAsync(unitActorId, cancellationToken);
            if (!string.Equals(currentRunId, completion.WorkflowInstanceId, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Unit {ActorId} ignoring validation completion from workflow {WorkflowInstanceId}: stale run (current {CurrentRunId}).",
                    unitActorId, completion.WorkflowInstanceId, currentRunId ?? "<none>");
                return new TransitionResult(
                    false, current,
                    "validation completion ignored: stale workflow run id");
            }
        }

        // A completion only makes sense from Validating; any other non-
        // terminal state means a transition was racing.
        if (current != UnitStatus.Validating)
        {
            logger.LogWarning(
                "Unit {ActorId} received validation completion but current status is {Status}; expected Validating.",
                unitActorId, current);
            return new TransitionResult(
                false, current,
                $"validation completion ignored: status is {current}, expected Validating");
        }

        if (completion.Success)
        {
            // Clear any prior failure payload first.
            if (tracker is not null)
            {
                await tracker.SetFailureAsync(unitActorId, null, cancellationToken);
            }

            return await persistTransition(UnitStatus.Validating, UnitStatus.Stopped, cancellationToken);
        }

        // Failure: serialize the payload and persist before the transition
        // write so any downstream reader of Error status also sees the
        // failure blob on the same row.
        if (tracker is not null)
        {
            var payload = completion.Failure is null
                ? null
                : JsonSerializer.Serialize(completion.Failure);
            await tracker.SetFailureAsync(unitActorId, payload, cancellationToken);
        }

        return await persistTransition(UnitStatus.Validating, UnitStatus.Error, cancellationToken);
    }

    /// <summary>
    /// Persists a scheduler-side failure: writes the structured error blob
    /// and transitions the unit out of <see cref="UnitStatus.Validating"/>
    /// into <see cref="UnitStatus.Error"/> via the actor's
    /// <paramref name="persistTransition"/> delegate. Best-effort: a
    /// tracker-write failure here does not block the recovery transition.
    /// </summary>
    private async Task<TransitionResult> PersistSchedulerFailureAsync(
        string unitActorId,
        UnitValidationError error,
        Func<UnitStatus, UnitStatus, CancellationToken, Task<TransitionResult>> persistTransition,
        CancellationToken cancellationToken)
    {
        if (tracker is not null)
        {
            try
            {
                var payload = JsonSerializer.Serialize(error);
                await tracker.SetFailureAsync(unitActorId, payload, cancellationToken);
            }
            catch (Exception persistEx)
            {
                logger.LogWarning(
                    persistEx,
                    "Unit {ActorId}: failed to persist scheduler-failure payload ({Code}) before Error transition.",
                    unitActorId, error.Code);
            }
        }

        return await persistTransition(UnitStatus.Validating, UnitStatus.Error, cancellationToken);
    }
}