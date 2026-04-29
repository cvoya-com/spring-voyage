// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Seam that encapsulates the membership-management concern extracted from
/// <c>UnitActor</c>: adding a member (including cycle-detection for
/// <c>unit://</c>-typed candidates), removing a member, and mirroring every
/// mutation into a persistent projection via the actor's
/// <c>subunit projector</c> callback.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host can
/// substitute a tenant-aware coordinator (e.g. one that enforces
/// cross-tenant guards or adds audit logging on every membership change)
/// without touching the actor. Per the platform's "interface-first + TryAdd*"
/// rule, production DI registers the default implementation with
/// <c>TryAddSingleton</c> so the private repo's registration takes precedence
/// when present.
/// </para>
/// <para>
/// The coordinator does not hold a reference to the actor. Both methods receive
/// delegates so the actor can inject its own state-read, state-write, and
/// event-emission implementations without the coordinator depending on Dapr
/// actor types.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual unit — it
/// operates entirely through the per-call delegates and the injected
/// <see cref="IDirectoryService"/> singleton. This makes it safe to register
/// as a singleton and share across all <c>UnitActor</c> instances.
/// </para>
/// </remarks>
public interface IUnitMembershipCoordinator
{
    /// <summary>
    /// Adds <paramref name="member"/> to the unit's member list, performing
    /// cycle detection for <c>unit://</c>-typed members before persisting the
    /// change. Idempotent: if <paramref name="member"/> is already present the
    /// method returns without modifying state.
    /// </summary>
    /// <param name="unitActorId">The Dapr actor id of the unit accepting the new member.</param>
    /// <param name="unitAddress">
    /// The address of the unit actor (e.g. <c>unit://team-alpha</c>). Used as
    /// the cycle-detection anchor and as the <c>ParentUnit</c> in any thrown
    /// <see cref="CyclicMembershipException"/>.
    /// </param>
    /// <param name="member">The member address to add.</param>
    /// <param name="getMembers">
    /// Delegate that reads the unit's current member list from actor state.
    /// </param>
    /// <param name="persistMembers">
    /// Delegate that writes the updated member list back to actor state and
    /// emits the corresponding <c>StateChanged</c> activity event.
    /// </param>
    /// <param name="resolveAddress">
    /// Delegate that resolves a <c>unit://</c> address to a
    /// <see cref="DirectoryEntry"/> (or <see langword="null"/> when the unit
    /// does not exist). Used to map path-form addresses to actor ids during
    /// the cycle-detection walk.
    /// </param>
    /// <param name="getSubUnitMembers">
    /// Delegate that returns the member list of a sub-unit identified by its
    /// Dapr actor id. The coordinator calls this for each node it visits
    /// during the BFS cycle-detection walk.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="CyclicMembershipException">
    /// Thrown when adding <paramref name="member"/> would introduce a cycle
    /// (self-loop, back-edge, or depth-bound exceeded).
    /// </exception>
    Task AddMemberAsync(
        string unitActorId,
        Address unitAddress,
        Address member,
        Func<CancellationToken, Task<List<Address>>> getMembers,
        Func<List<Address>, CancellationToken, Task> persistMembers,
        Func<Address, CancellationToken, Task<DirectoryEntry?>> resolveAddress,
        Func<string, CancellationToken, Task<Address[]>> getSubUnitMembers,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes <paramref name="member"/> from the unit's member list. Idempotent:
    /// if <paramref name="member"/> is not present the method returns without
    /// modifying state.
    /// </summary>
    /// <param name="unitActorId">The Dapr actor id of the unit losing the member.</param>
    /// <param name="member">The member address to remove.</param>
    /// <param name="getMembers">
    /// Delegate that reads the unit's current member list from actor state.
    /// </param>
    /// <param name="persistMembers">
    /// Delegate that writes the updated member list back to actor state and
    /// emits the corresponding <c>StateChanged</c> activity event.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task RemoveMemberAsync(
        string unitActorId,
        Address member,
        Func<CancellationToken, Task<List<Address>>> getMembers,
        Func<List<Address>, CancellationToken, Task> persistMembers,
        CancellationToken cancellationToken = default);
}