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

    // #2044 / ADR-0040: the human-side dual view of unit permissions
    // (Get/Set/RemovePermissionForUnitAsync, backed by Human:UnitPermissions)
    // is gone. Unit ACLs live in unit_human_permissions; the unit-side
    // surface (IUnitActor.SetHumanPermissionAsync /
    // GetHumanPermissionAsync / RemoveHumanPermissionAsync) is the single
    // grant API. Callers that need to enumerate the units a human has
    // access to query the table directly.

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
