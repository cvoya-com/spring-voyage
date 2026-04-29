// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;

/// <summary>
/// Seam that encapsulates the permission-management concern extracted from
/// <c>UnitActor</c>: granting, revoking, querying, and reading the
/// inheritance flag for human permissions on a unit.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Dapr.Units</c> alongside
/// <see cref="IUnitMembershipCoordinator"/> and
/// <see cref="IUnitValidationCoordinator"/> so the cloud host can substitute a
/// tenant-aware coordinator (e.g. one that enforces cross-tenant permission
/// guards or adds audit logging on every grant / revocation) without touching
/// the actor. Per the platform's "interface-first + TryAdd*" rule, production
/// DI registers the default implementation with <c>TryAddSingleton</c> so the
/// private repo's registration takes precedence when present.
/// </para>
/// <para>
/// The coordinator does not hold a reference to the actor. Every method
/// receives delegates so the actor can inject its own state-read,
/// state-write, and event-emission implementations without the coordinator
/// depending on Dapr actor types.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual unit — it
/// operates entirely through the per-call delegates. This makes it safe to
/// register as a singleton and share across all <c>UnitActor</c> instances.
/// </para>
/// <para>
/// ADR-0013 establishes the inheritance model (nearest-grant-wins,
/// ancestor-cascade-by-default, fail-closed). The coordinator preserves that
/// model by managing <see cref="UnitPermissionInheritance"/> through the
/// <see cref="GetPermissionInheritanceAsync"/> and
/// <see cref="SetPermissionInheritanceAsync"/> methods. The actual hierarchy
/// walk lives in <c>IPermissionService.ResolveEffectivePermissionAsync</c>.
/// </para>
/// </remarks>
public interface IUnitPermissionCoordinator
{
    /// <summary>
    /// Stores or replaces the permission entry for <paramref name="humanId"/>
    /// on the unit identified by <paramref name="unitActorId"/>.
    /// </summary>
    /// <param name="unitActorId">The Dapr actor id of the unit.</param>
    /// <param name="humanId">The identity of the human whose permission is being set.</param>
    /// <param name="entry">The permission entry to store.</param>
    /// <param name="getPermissions">
    /// Delegate that reads the unit's current human-permissions map from
    /// actor state.
    /// </param>
    /// <param name="persistPermissions">
    /// Delegate that writes the updated permissions map back to actor state.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task SetHumanPermissionAsync(
        string unitActorId,
        string humanId,
        UnitPermissionEntry entry,
        Func<CancellationToken, Task<Dictionary<string, UnitPermissionEntry>>> getPermissions,
        Func<Dictionary<string, UnitPermissionEntry>, CancellationToken, Task> persistPermissions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the direct <see cref="PermissionLevel"/> for
    /// <paramref name="humanId"/> on the unit, or <see langword="null"/>
    /// when no direct grant exists.
    /// </summary>
    /// <param name="unitActorId">The Dapr actor id of the unit.</param>
    /// <param name="humanId">The identity of the human to look up.</param>
    /// <param name="getPermissions">
    /// Delegate that reads the unit's current human-permissions map from
    /// actor state.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The permission level when a direct grant exists; otherwise
    /// <see langword="null"/>.
    /// </returns>
    Task<PermissionLevel?> GetHumanPermissionAsync(
        string unitActorId,
        string humanId,
        Func<CancellationToken, Task<Dictionary<string, UnitPermissionEntry>>> getPermissions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the permission entry for <paramref name="humanId"/> from the
    /// unit. Idempotent: if no entry exists the method returns
    /// <see langword="false"/> without modifying state.
    /// </summary>
    /// <param name="unitActorId">The Dapr actor id of the unit.</param>
    /// <param name="humanId">The identity of the human whose permission is being removed.</param>
    /// <param name="getPermissions">
    /// Delegate that reads the unit's current human-permissions map from
    /// actor state.
    /// </param>
    /// <param name="persistPermissions">
    /// Delegate that writes the updated permissions map back to actor state.
    /// Called only when an entry was actually removed.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when an entry was removed;
    /// <see langword="false"/> when no matching entry existed (the caller
    /// still returns HTTP 204 — idempotent by contract).
    /// </returns>
    Task<bool> RemoveHumanPermissionAsync(
        string unitActorId,
        string humanId,
        Func<CancellationToken, Task<Dictionary<string, UnitPermissionEntry>>> getPermissions,
        Func<Dictionary<string, UnitPermissionEntry>, CancellationToken, Task> persistPermissions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all human permission entries currently stored on the unit.
    /// </summary>
    /// <param name="unitActorId">The Dapr actor id of the unit.</param>
    /// <param name="getPermissions">
    /// Delegate that reads the unit's current human-permissions map from
    /// actor state.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<UnitPermissionEntry[]> GetHumanPermissionsAsync(
        string unitActorId,
        Func<CancellationToken, Task<Dictionary<string, UnitPermissionEntry>>> getPermissions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the unit's current <see cref="UnitPermissionInheritance"/> flag
    /// from actor state, defaulting to
    /// <see cref="UnitPermissionInheritance.Inherit"/> when no value has been
    /// persisted (ADR-0013: ancestor grants cascade by default).
    /// </summary>
    /// <param name="unitActorId">The Dapr actor id of the unit.</param>
    /// <param name="getInheritance">
    /// Delegate that reads the persisted inheritance flag from actor state.
    /// Returns <see langword="null"/> when the state key is absent.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<UnitPermissionInheritance> GetPermissionInheritanceAsync(
        string unitActorId,
        Func<CancellationToken, Task<UnitPermissionInheritance?>> getInheritance,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the unit's <see cref="UnitPermissionInheritance"/> flag.
    /// Writing <see cref="UnitPermissionInheritance.Inherit"/> removes the
    /// state key (representing the default) rather than storing a no-op value,
    /// consistent with the boundary actor's row-deletion pattern and ADR-0013's
    /// fail-closed posture.
    /// </summary>
    /// <param name="unitActorId">The Dapr actor id of the unit.</param>
    /// <param name="inheritance">The inheritance mode to apply.</param>
    /// <param name="persistInheritance">
    /// Delegate that writes the inheritance flag to actor state and emits the
    /// corresponding <c>StateChanged</c> activity event.
    /// </param>
    /// <param name="removeInheritance">
    /// Delegate that removes the inheritance state key (called when
    /// <paramref name="inheritance"/> is
    /// <see cref="UnitPermissionInheritance.Inherit"/> to restore the default
    /// without leaving a no-op state entry).
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task SetPermissionInheritanceAsync(
        string unitActorId,
        UnitPermissionInheritance inheritance,
        Func<UnitPermissionInheritance, CancellationToken, Task> persistInheritance,
        Func<CancellationToken, Task> removeInheritance,
        CancellationToken cancellationToken = default);
}