// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Seam for persisting the per-unit validation-tracking columns added in
/// T-02 (<c>LastValidationRunId</c> and <c>LastValidationErrorJson</c>)
/// without coupling <c>UnitActor</c> to Entity Framework Core or the
/// physical <c>UnitDefinitionEntity</c> row. The Dapr package implements
/// this on top of the shared <c>SpringDbContext</c>; the cloud host can
/// layer a tenant-aware decorator via <c>TryAdd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Lookups are keyed by <c>UnitDefinitionEntity.ActorId</c> because
/// <c>UnitActor</c> only knows its Dapr actor id — the user-facing unit
/// name lives on the directory row. Every write is a focused update on a
/// single row: the orchestration store's larger write semantics (rewrite
/// the entire Definition JSON) are deliberately kept out of this contract.
/// </para>
/// <para>
/// All methods are no-ops when no row is found for the given actor id,
/// matching the tolerance contract of <c>DbUnitExecutionStore</c>: a
/// missing row never throws.
/// </para>
/// </remarks>
public interface IUnitValidationTracker
{
    /// <summary>
    /// Reads the current <c>LastValidationRunId</c> for the unit, or
    /// <c>null</c> when none is set / the row is missing.
    /// </summary>
    /// <param name="unitActorId">The unit's Dapr actor id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<string?> GetLastValidationRunIdAsync(
        string unitActorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets <c>LastValidationRunId</c> to <paramref name="runId"/> and
    /// atomically clears <c>LastValidationErrorJson</c> so an observer
    /// sees "clean slate + fresh run id" rather than "stale error plus
    /// new run id." Called by <c>UnitActor</c> on every transition into
    /// <see cref="UnitStatus.Validating"/>.
    /// </summary>
    /// <param name="unitActorId">The unit's Dapr actor id.</param>
    /// <param name="runId">The new workflow instance id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task BeginRunAsync(
        string unitActorId,
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists <paramref name="errorJson"/> (a System.Text.Json-serialized
    /// <see cref="UnitValidationError"/>) to <c>LastValidationErrorJson</c>.
    /// Called by <c>UnitActor.CompleteValidationAsync</c> when the workflow
    /// reports a failure.
    /// </summary>
    /// <param name="unitActorId">The unit's Dapr actor id.</param>
    /// <param name="errorJson">The JSON payload, or <c>null</c> to clear.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SetFailureAsync(
        string unitActorId,
        string? errorJson,
        CancellationToken cancellationToken = default);
}
