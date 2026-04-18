// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Resolves the effective permission level for a human within a specific unit.
/// </summary>
public interface IPermissionService
{
    /// <summary>
    /// Returns the <em>direct</em> permission grant recorded on
    /// <paramref name="unitId"/> for <paramref name="humanId"/>, ignoring the
    /// unit hierarchy. Does not consult ancestor units. Kept for backward
    /// compatibility and for the unit-editor surfaces that need to answer
    /// "is there an explicit grant here?" independently of inheritance.
    /// </summary>
    /// <param name="humanId">The human's identifier.</param>
    /// <param name="unitId">The unit's identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The direct <see cref="PermissionLevel"/>, or <c>null</c> if no direct grant exists.</returns>
    Task<PermissionLevel?> ResolvePermissionAsync(string humanId, string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the <em>effective</em> permission level for <paramref name="humanId"/>
    /// on <paramref name="unitId"/>, walking up the unit hierarchy so that
    /// ancestor grants cascade down to descendant units by default (#414).
    /// This is the value the <see cref="PermissionHandler"/> consults when
    /// authorizing HTTP requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution rules:
    /// </para>
    /// <list type="number">
    ///   <item><description>A direct grant on the target unit always wins —
    ///     including a deliberate downgrade. A child unit that grants a human
    ///     <see cref="PermissionLevel.Viewer"/> is never silently promoted to
    ///     Owner because an ancestor happens to grant Owner.</description></item>
    ///   <item><description>If the target unit has no direct grant, the
    ///     resolver walks ancestor units (nearest first) and returns the
    ///     first non-<c>null</c> grant it finds. The walk takes the strongest
    ///     grant along the nearest ancestor — a parent that grants Operator
    ///     cascades as Operator, not as Owner.</description></item>
    ///   <item><description>The walk stops at any unit whose
    ///     <see cref="UnitPermissionInheritance"/> mode is
    ///     <see cref="UnitPermissionInheritance.Isolated"/>. An isolated unit
    ///     is the permission-layer analogue of an opaque boundary (#413):
    ///     ancestor authority does not flow through it. Direct grants on the
    ///     isolated unit or on its own descendants still apply.</description></item>
    ///   <item><description>The walk is bounded by the same depth cap that
    ///     <c>UnitActor</c> uses for membership cycle detection (64). A
    ///     pathological graph never loops; it surfaces as "no ancestor
    ///     grant found" and the caller is denied.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="humanId">The human's identifier.</param>
    /// <param name="unitId">The unit's identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The effective <see cref="PermissionLevel"/>, or <c>null</c> when the human has no direct or inherited permission.</returns>
    Task<PermissionLevel?> ResolveEffectivePermissionAsync(string humanId, string unitId, CancellationToken cancellationToken = default);
}