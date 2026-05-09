// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Seam for scheduling the Dapr <c>UnitValidationWorkflow</c> on behalf of
/// the unit actor. The actor calls this interface whenever it transitions
/// into <see cref="UnitStatus.Validating"/>; the implementation in the
/// <c>Cvoya.Spring.Dapr</c> package resolves the unit's execution defaults
/// (image, runtime, credential, model), schedules the workflow via
/// <c>DaprWorkflowClient</c>, and returns the instance id so the actor can
/// persist it to <see cref="UnitDefinitionEntity.LastValidationRunId"/>.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host can
/// substitute a tenant-aware scheduler (e.g. one that dispatches the
/// workflow to a tenant-scoped Dapr app id) without touching the actor.
/// Per the platform's "interface-first + TryAdd*" rule, production DI
/// registers the Dapr-backed scheduler with <c>TryAddSingleton</c> so the
/// private repo's registration takes precedence when present.
/// </para>
/// <para>
/// Returning the unit's name alongside the workflow instance id lets the
/// actor's stale-run guard compare against
/// <see cref="UnitDefinitionEntity.LastValidationRunId"/> later, and lets
/// the workflow emit <c>ValidationProgress</c> events scoped to the unit's
/// name so the web detail page's SSE filter (which keys on the unit's
/// user-facing name, not its actor Guid) picks them up.
/// </para>
/// </remarks>
public interface IUnitValidationWorkflowScheduler
{
    /// <summary>
    /// Schedules a new <c>UnitValidationWorkflow</c> run for the unit
    /// identified by <paramref name="unitActorId"/>. Returns the workflow
    /// instance id plus the unit's user-facing name so the actor can
    /// persist both on the transition write.
    /// </summary>
    /// <param name="unitActorId">The unit's Dapr actor id — the same value surfaced by <c>Actor.Id.GetId()</c>.</param>
    /// <param name="cancellationToken">Cancels the schedule.</param>
    /// <returns>A <see cref="UnitValidationSchedule"/> describing the scheduled run.</returns>
    Task<UnitValidationSchedule> ScheduleAsync(
        string unitActorId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of <see cref="IUnitValidationWorkflowScheduler.ScheduleAsync"/>.
/// </summary>
/// <param name="WorkflowInstanceId">
/// Dapr workflow instance id returned by the workflow engine. Persisted on
/// the unit's <c>LastValidationRunId</c> column so the terminal callback
/// can detect stale runs (an older workflow arriving after a newer one has
/// already completed).
/// </param>
/// <param name="UnitName">
/// The unit's user-facing name — used by the workflow's progress events as
/// their <c>Address.Path</c> so the portal's SSE filter (which keys on the
/// name, not the actor id) matches them.
/// </param>
public sealed record UnitValidationSchedule(
    string WorkflowInstanceId,
    string UnitName);
