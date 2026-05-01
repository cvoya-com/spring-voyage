// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Dapr actor interface for human actors. Humans share the
/// <see cref="IAgent"/> mailbox / message-dispatch contract so the router
/// can deliver messages to humans the same way it delivers to agents and
/// units. In addition, humans carry identity, permissions, and
/// notification preferences.
/// </summary>
public interface IHumanActor : IAgent
{
    /// <summary>
    /// Gets the current global permission level of the human actor.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The current <see cref="PermissionLevel"/>.</returns>
    Task<PermissionLevel> GetPermissionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the permission level for this human within a specific unit.
    /// </summary>
    /// <param name="unitId">The unit identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The <see cref="PermissionLevel"/> for the specified unit, or <c>null</c> if not set.</returns>
    Task<PermissionLevel?> GetPermissionForUnitAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the permission level for this human within a specific unit.
    /// </summary>
    /// <param name="unitId">The unit identifier.</param>
    /// <param name="level">The permission level to set.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetPermissionForUnitAsync(string unitId, PermissionLevel level, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes this human's unit-scoped permission entry for <paramref name="unitId"/>.
    /// Idempotent — clearing an entry that was never set is a no-op. Paired
    /// with <see cref="IUnitActor.RemoveHumanPermissionAsync"/> so the
    /// unit-side and human-side views stay consistent after
    /// <c>spring unit humans remove</c>.
    /// </summary>
    /// <param name="unitId">The unit identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task RemovePermissionForUnitAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records <paramref name="readAt"/> as the last time this human opened
    /// <paramref name="threadId"/>. Idempotent — calling it multiple times
    /// with increasing timestamps advances the cursor; calling it with an
    /// older timestamp is a no-op (the stored value is never moved backwards).
    /// </summary>
    /// <param name="threadId">The thread that was read.</param>
    /// <param name="readAt">The timestamp to record as the read cursor.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task MarkReadAsync(string threadId, DateTimeOffset readAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the per-thread read cursor array — one <see cref="ThreadReadEntry"/>
    /// per thread the human has read. Returns an empty array when no threads have
    /// been read yet (lazy initialisation).
    /// </summary>
    /// <remarks>
    /// Bug #319: returning a concrete array avoids <c>DataContractSerializer</c>
    /// "type not expected" failures at the Dapr remoting boundary. Dictionary
    /// types are not data-contract known types by default, so the public contract
    /// must be an array of a <c>[DataContract]</c>-annotated record.
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<ThreadReadEntry[]> GetLastReadAtAsync(CancellationToken cancellationToken = default);
}