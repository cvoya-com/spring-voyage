// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Membership-management seam for <c>UnitActor</c>: adding a member
/// (including cycle-detection for <c>unit://</c>-typed candidates) and
/// removing a member, both routed through <see cref="IUnitMemberGraphStore"/>
/// so <c>unit_memberships</c> / <c>unit_subunit_memberships</c> are the
/// single source of truth (#2052 / ADR-0040).
/// </summary>
/// <remarks>
/// <para>
/// Defined in <c>Cvoya.Spring.Core</c> so the cloud host can substitute a
/// tenant-aware coordinator (e.g. one that adds audit logging on every
/// membership change) without touching the actor. Per the platform's
/// "interface-first + TryAdd*" rule, production DI registers the default
/// implementation with <c>TryAddSingleton</c> so the private repo's
/// registration takes precedence when present.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual unit — it
/// operates entirely against the injected <see cref="IUnitMemberGraphStore"/>
/// (which itself wraps a per-call EF scope). This keeps it safe to register
/// as a singleton.
/// </para>
/// </remarks>
public interface IUnitMembershipCoordinator
{
    /// <summary>
    /// Adds <paramref name="member"/> to the unit's member graph in EF,
    /// performing cycle detection for <c>unit://</c>-typed members before
    /// persisting. Idempotent: if the edge already exists the method
    /// returns without modifying state. Emits a <c>StateChanged</c>
    /// activity event via <paramref name="emitStateChanged"/> when (and
    /// only when) a new edge is written.
    /// </summary>
    /// <param name="unitId">The stable Guid identity of the parent unit.</param>
    /// <param name="unitAddress">
    /// The address of the parent unit (e.g. <c>unit://{guid}</c>). Used as
    /// the cycle-detection anchor and as the <c>ParentUnit</c> in any
    /// thrown <see cref="CyclicMembershipException"/>.
    /// </param>
    /// <param name="member">The member address to add.</param>
    /// <param name="emitStateChanged">
    /// Delegate the actor passes so the coordinator can emit the
    /// <c>StateChanged</c> activity event with the actor's standard
    /// envelope shape after a successful write.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="CyclicMembershipException">
    /// Thrown when adding <paramref name="member"/> would introduce a cycle
    /// (self-loop, back-edge, or depth-bound exceeded).
    /// </exception>
    Task AddMemberAsync(
        Guid unitId,
        Address unitAddress,
        Address member,
        Func<Address, int, CancellationToken, Task> emitStateChanged,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes <paramref name="member"/> from the unit's member graph in
    /// EF. Idempotent: if the edge is absent the method returns without
    /// modifying state. Emits a <c>StateChanged</c> event via
    /// <paramref name="emitStateChanged"/> when (and only when) a row
    /// was deleted.
    /// </summary>
    /// <param name="unitId">The stable Guid identity of the parent unit.</param>
    /// <param name="member">The member address to remove.</param>
    /// <param name="emitStateChanged">
    /// Delegate the actor passes so the coordinator can emit the
    /// <c>StateChanged</c> activity event with the actor's standard
    /// envelope shape after a successful delete.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task RemoveMemberAsync(
        Guid unitId,
        Address member,
        Func<Address, int, CancellationToken, Task> emitStateChanged,
        CancellationToken cancellationToken = default);
}
