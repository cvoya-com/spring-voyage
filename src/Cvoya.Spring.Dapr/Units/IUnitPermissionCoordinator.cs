// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using Cvoya.Spring.Dapr.Auth;

/// <summary>
/// Seam that encapsulates the unit's permission-inheritance flag — the
/// <see cref="UnitPermissionInheritance"/> value the
/// hierarchy-aware permission resolver consults to decide whether ancestor
/// grants flow through this unit.
/// </summary>
/// <remarks>
/// <para>
/// Pre-#2044 this seam also owned the per-unit (humanId → entry) map. After
/// #2044 / ADR-0040, ACL grants live in the <c>unit_human_permissions</c>
/// EF table and are written / read through <see cref="IUnitHumanPermissionStore"/>;
/// the coordinator now only owns the inheritance-flag concern. The
/// inheritance flag itself stays on actor state for v0.1 and moves to
/// <c>unit_live_config</c> in
/// <see href="https://github.com/cvoya-com/spring-voyage/issues/2049">#2049</see>.
/// </para>
/// <para>
/// The coordinator does not hold a reference to the actor. Every method
/// receives delegates so the actor can inject its own state-read,
/// state-write, and event-emission implementations without the coordinator
/// depending on Dapr actor types. The coordinator is stateless with respect
/// to any individual unit, which makes it safe to register as a singleton
/// and share across all <c>UnitActor</c> instances.
/// </para>
/// <para>
/// ADR-0013 establishes the inheritance model (nearest-grant-wins,
/// ancestor-cascade-by-default, fail-closed). The actual hierarchy walk
/// lives in <c>IPermissionService.ResolveEffectivePermissionAsync</c>.
/// </para>
/// </remarks>
public interface IUnitPermissionCoordinator
{
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
