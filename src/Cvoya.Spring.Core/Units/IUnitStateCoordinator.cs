// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Seam that encapsulates the persisted-config CRUD concern extracted
/// from <c>UnitActor</c>: reading and writing the unit's metadata,
/// boundary, permission-inheritance flag, and own-expertise list, and
/// emitting the corresponding <see cref="ActivityEventType.StateChanged"/>
/// activity events.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host
/// can substitute a tenant-aware coordinator (e.g. one that layers
/// audit logging on every metadata write, or gates boundary changes on
/// per-tenant allowlists) without touching the actor. Per the
/// platform's "interface-first + TryAdd*" rule, production DI registers
/// the default implementation with <c>TryAddSingleton</c> so the
/// private repo's registration takes precedence when present.
/// </para>
/// <para>
/// Per ADR-0040 / #2049 every method is now backed by EF
/// (<c>unit_live_config</c>, <c>unit_expertise</c>). The actor passes
/// its actor id; the coordinator owns the EF read/write through an
/// injected store. There are no actor-state delegates because there is
/// no actor-state copy to keep in sync.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual unit —
/// the per-unit identifier is a parameter on every method. This makes
/// it safe to register as a singleton and share across all
/// <c>UnitActor</c> instances.
/// </para>
/// </remarks>
public interface IUnitStateCoordinator
{
    /// <summary>
    /// Reads the unit's persisted metadata from EF and returns it as a
    /// <see cref="UnitMetadata"/> record. <see cref="UnitMetadata.DisplayName"/>
    /// and <see cref="UnitMetadata.Description"/> are always <c>null</c>
    /// on the returned record — the directory entity
    /// (<c>unit_definitions</c>) is the authoritative source for those
    /// fields. API surfaces that need the directory-owned fields
    /// compose <c>GetMetadataAsync</c> output with a directory lookup.
    /// </summary>
    /// <param name="unitId">The Dapr actor id of the unit.</param>
    /// <param name="cancellationToken">Cancels the read operation.</param>
    Task<UnitMetadata> GetMetadataAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes whichever Model / Color / Provider / Hosting fields of
    /// <paramref name="metadata"/> are non-<c>null</c> to the unit's
    /// <c>unit_live_config</c> row and emits a
    /// <see cref="ActivityEventType.StateChanged"/> event. Does
    /// nothing (and emits no event) when every applicable field is
    /// <c>null</c>. <see cref="UnitMetadata.DisplayName"/> and
    /// <see cref="UnitMetadata.Description"/> are silently ignored —
    /// directory writes go through the directory endpoints (ADR-0040).
    /// </summary>
    /// <param name="unitId">The Dapr actor id of the unit.</param>
    /// <param name="metadata">The partial patch to apply.</param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the
    /// activity bus. Called once with a
    /// <see cref="ActivityEventType.StateChanged"/> event after all
    /// applicable fields have been persisted.
    /// </param>
    /// <param name="cancellationToken">Cancels the write operation.</param>
    Task SetMetadataAsync(
        string unitId,
        UnitMetadata metadata,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the unit's persisted boundary from EF, returning
    /// <see cref="UnitBoundary.Empty"/> when no rules are set.
    /// </summary>
    Task<UnitBoundary> GetBoundaryAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the unit's boundary in full and emits a
    /// <see cref="ActivityEventType.StateChanged"/> event.
    /// </summary>
    Task SetBoundaryAsync(
        string unitId,
        UnitBoundary boundary,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the unit's permission-inheritance flag, defaulting to
    /// <c>Inherit</c> when no value has been persisted (ADR-0013).
    /// Returned as <see cref="int"/> for actor-remoting safety; the
    /// concrete enum lives in the Dapr layer.
    /// </summary>
    Task<int> GetPermissionInheritanceAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the unit's permission-inheritance flag and emits a
    /// <see cref="ActivityEventType.StateChanged"/> event. The flag is
    /// passed as <see cref="int"/> (the enum's ordinal) because the
    /// concrete <c>UnitPermissionInheritance</c> enum lives in the
    /// Dapr layer; Core declines to take a dependency on it.
    /// </summary>
    Task SetPermissionInheritanceAsync(
        string unitId,
        int inheritance,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the unit's own-expertise list from EF, sorted by domain
    /// name. Returns an empty array when no rows exist.
    /// </summary>
    Task<ExpertiseDomain[]> GetOwnExpertiseAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the unit's own-expertise list in full. Empty list is
    /// a legitimate "no own-expertise" state and still flips the
    /// unit's <c>expertise_initialised</c> flag so the activation
    /// seeder honours the actor-state-wins precedence rule (#488).
    /// Emits a <see cref="ActivityEventType.StateChanged"/> event
    /// after the write commits.
    /// </summary>
    Task SetOwnExpertiseAsync(
        string unitId,
        ExpertiseDomain[] domains,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when an own-expertise list has been
    /// written for <paramref name="unitId"/> at all (even an empty
    /// list). Used by the activation seeder so an explicit operator
    /// edit (including a deliberate clear) is preserved across
    /// process restart even when the persisted list happens to be
    /// empty.
    /// </summary>
    Task<bool> HasOwnExpertiseSetAsync(string unitId, CancellationToken cancellationToken = default);
}
