// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Lifecycle;

using Cvoya.Spring.Core.Artefacts;

/// <summary>
/// Queryable mirror of an artefact's <see cref="LifecycleStatus"/> (#2981).
/// The canonical status lives in Dapr actor state, which only the owning
/// actor turn can read without contending on the non-reentrant turn lock —
/// so external enforcement points (the dispatcher cold-start gate, the
/// message-router delivery gate) and the portal status read-path cannot
/// consult it cheaply, and historically raced <c>GetStatusAsync</c> against a
/// budget and fabricated <see cref="LifecycleStatus.Starting"/> on timeout.
/// <para>
/// This seam exposes the same status as a row on the existing
/// <c>agent_live_config</c> / <c>unit_live_config</c> tables (1:1 with the
/// artefact, persisting across stop/restart). The owning actor writes through
/// it on every lifecycle transition; everyone else reads it without touching
/// the actor. The mirror is advisory for correctness — the actor's own state
/// remains the authority and backstops a stale read — but authoritative
/// enough for the gates, because the write happens inside the same
/// transition turn that flips the actor state.
/// </para>
/// </summary>
public interface ILifecycleStatusStore
{
    /// <summary>
    /// Upserts the mirrored lifecycle status for an artefact. Called by the
    /// owning actor inside its transition turn, after the authoritative
    /// actor-state write. Creates the backing live-config row when absent
    /// (a first transition can precede the first metadata write).
    /// </summary>
    /// <param name="kind">The artefact kind (<see cref="ArtefactKind.Unit"/> or <see cref="ArtefactKind.Agent"/>).</param>
    /// <param name="artefactId">The artefact's stable Guid identity.</param>
    /// <param name="status">The new lifecycle status to mirror.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetStatusAsync(
        ArtefactKind kind,
        Guid artefactId,
        LifecycleStatus status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the mirrored lifecycle status for an artefact, or <c>null</c>
    /// when no row exists yet (the artefact has never transitioned, so callers
    /// must treat the absence as "not halted" and let the actor be the
    /// authority).
    /// </summary>
    /// <param name="kind">The artefact kind (<see cref="ArtefactKind.Unit"/> or <see cref="ArtefactKind.Agent"/>).</param>
    /// <param name="artefactId">The artefact's stable Guid identity.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<LifecycleStatus?> TryGetStatusAsync(
        ArtefactKind kind,
        Guid artefactId,
        CancellationToken cancellationToken = default);
}
