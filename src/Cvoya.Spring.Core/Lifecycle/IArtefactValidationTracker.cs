// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Lifecycle;

using Cvoya.Spring.Core.Artefacts;

/// <summary>
/// Seam for persisting the per-artefact validation-tracking columns added in
/// T-02 (<c>LastValidationRunId</c> and <c>LastValidationErrorJson</c>)
/// without coupling the actors to Entity Framework Core or the physical
/// entity rows. The Dapr package implements this on top of the shared
/// <c>SpringDbContext</c>; the cloud host can layer a tenant-aware
/// decorator via <c>TryAdd</c>. #2364 generalised the contract from
/// unit-only to artefact-keyed: <see cref="ArtefactKind.Unit"/> routes to
/// <c>UnitDefinitionEntity</c>, <see cref="ArtefactKind.Agent"/> routes to
/// <c>AgentDefinitionEntity</c>.
/// </summary>
/// <remarks>
/// <para>
/// Lookups are keyed by the entity's <c>ActorId</c> because the actor only
/// knows its Dapr actor id — the user-facing name lives on the directory
/// row. Every write is a focused update on a single row: the orchestration
/// store's larger write semantics (rewrite the entire Definition JSON) are
/// deliberately kept out of this contract.
/// </para>
/// <para>
/// All methods are no-ops when no row is found for the given actor id,
/// matching the tolerance contract of the per-kind execution stores: a
/// missing row never throws.
/// </para>
/// </remarks>
public interface IArtefactValidationTracker
{
    /// <summary>
    /// Reads the current <c>LastValidationRunId</c> for the artefact, or
    /// <c>null</c> when none is set / the row is missing.
    /// </summary>
    Task<string?> GetLastValidationRunIdAsync(
        ArtefactKind kind,
        string artefactActorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets <c>LastValidationRunId</c> to <paramref name="runId"/> and
    /// atomically clears <c>LastValidationErrorJson</c> so an observer
    /// sees "clean slate + fresh run id" rather than "stale error plus
    /// new run id." Called by the actor on every transition into
    /// <see cref="LifecycleStatus.Validating"/>.
    /// </summary>
    Task BeginRunAsync(
        ArtefactKind kind,
        string artefactActorId,
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists <paramref name="errorJson"/> (a System.Text.Json-serialized
    /// <see cref="ArtefactValidationError"/>) to <c>LastValidationErrorJson</c>.
    /// Called by the actor's <c>CompleteValidationAsync</c> when the workflow
    /// reports a failure.
    /// </summary>
    Task SetFailureAsync(
        ArtefactKind kind,
        string artefactActorId,
        string? errorJson,
        CancellationToken cancellationToken = default);
}
