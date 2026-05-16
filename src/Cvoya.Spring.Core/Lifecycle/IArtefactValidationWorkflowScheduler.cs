// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Lifecycle;

using Cvoya.Spring.Core.Artefacts;

/// <summary>
/// Seam for scheduling the Dapr <c>ArtefactValidationWorkflow</c> on behalf of
/// a unit or agent actor. The actor calls this interface whenever it
/// transitions into <see cref="LifecycleStatus.Validating"/>; the
/// implementation in the <c>Cvoya.Spring.Dapr</c> package resolves the
/// artefact's execution defaults (image, runtime, credential, model) from the
/// per-kind execution store, schedules the workflow via
/// <c>DaprWorkflowClient</c>, and returns the instance id so the actor can
/// persist it.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host can
/// substitute a tenant-aware scheduler without touching the actor. Per the
/// platform's "interface-first + TryAdd*" rule, production DI registers the
/// Dapr-backed scheduler with <c>TryAddSingleton</c> so the private repo's
/// registration takes precedence when present.
/// </para>
/// <para>
/// Returning the artefact's name alongside the workflow instance id lets the
/// actor's stale-run guard compare it later, and lets the workflow emit
/// <c>ValidationProgress</c> events scoped to the artefact's name so the web
/// detail page's SSE filter (which keys on the user-facing name, not the
/// actor Guid) picks them up.
/// </para>
/// </remarks>
public interface IArtefactValidationWorkflowScheduler
{
    /// <summary>
    /// Schedules a new <c>ArtefactValidationWorkflow</c> run for the artefact
    /// identified by <paramref name="kind"/> + <paramref name="artefactActorId"/>.
    /// Returns the workflow instance id plus the artefact's user-facing name
    /// so the actor can persist both on the transition write.
    /// </summary>
    /// <param name="kind">Whether the artefact is a Unit or an Agent.</param>
    /// <param name="artefactActorId">The artefact's Dapr actor id — the same value surfaced by <c>Actor.Id.GetId()</c>.</param>
    /// <param name="cancellationToken">Cancels the schedule.</param>
    /// <returns>A <see cref="ArtefactValidationSchedule"/> describing the scheduled run.</returns>
    Task<ArtefactValidationSchedule> ScheduleAsync(
        ArtefactKind kind,
        string artefactActorId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of <see cref="IArtefactValidationWorkflowScheduler.ScheduleAsync"/>.
/// </summary>
/// <param name="WorkflowInstanceId">
/// Dapr workflow instance id returned by the workflow engine. Persisted on
/// the artefact's <c>LastValidationRunId</c> column so the terminal callback
/// can detect stale runs.
/// </param>
/// <param name="ArtefactName">
/// The artefact's user-facing name — used by the workflow's progress events
/// as their <c>Address.Path</c> so the portal's SSE filter matches them.
/// </param>
public sealed record ArtefactValidationSchedule(
    string WorkflowInstanceId,
    string ArtefactName);
