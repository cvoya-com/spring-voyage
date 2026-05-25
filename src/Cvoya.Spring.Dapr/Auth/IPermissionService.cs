// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Resolves the effective <see cref="PermissionLevel"/> for an authenticated
/// caller against a specific unit. Per ADR-0047 §1 and #2768 the caller is
/// represented as an <see cref="Address"/> — a tenant-user when surfacing
/// from <see cref="Cvoya.Spring.Host.Api.Auth.IAuthenticatedCallerAccessor"/>,
/// a human when a package-declared role-slot acts on its own behalf — so the
/// permission model can short-circuit operator implicit-owner without forcing
/// every caller through the human-keyed grant table.
/// </summary>
public interface IPermissionService
{
    /// <summary>
    /// Returns the <em>direct</em> permission grant recorded on
    /// <paramref name="unitId"/> for <paramref name="caller"/>, ignoring the
    /// unit hierarchy. Does not consult ancestor units. Kept for unit-editor
    /// surfaces that need to answer "is there an explicit grant here?"
    /// independently of inheritance.
    /// </summary>
    /// <param name="caller">The authenticated caller's address.</param>
    /// <param name="unitId">The unit's identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The direct <see cref="PermissionLevel"/>, or <c>null</c> if no direct grant exists.</returns>
    Task<PermissionLevel?> ResolvePermissionAsync(Address caller, Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the <em>effective</em> permission level for <paramref name="caller"/>
    /// on <paramref name="unitId"/>, walking up the unit hierarchy so that
    /// ancestor grants cascade down to descendant units by default (#414).
    /// This is the value the <see cref="PermissionHandler"/> consults when
    /// authorising HTTP requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-scheme behaviour (OSS defaults; cloud overlays may swap via DI):
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>tenant-user://&lt;operator&gt;</c> — implicit
    ///     <see cref="PermissionLevel.Owner"/> on every unit in the tenant
    ///     (#2768). The OSS deployment ships with exactly one TenantUser, the
    ///     operator, so an explicit grant row carries no information.</description></item>
    ///   <item><description><c>human://&lt;id&gt;</c> — read against the
    ///     <c>unit_human_permissions</c> table, walking ancestor units for
    ///     inherited grants. A direct grant on the target unit always wins —
    ///     including a deliberate downgrade. The walk stops at an
    ///     <see cref="UnitPermissionInheritance.Isolated"/> boundary and is
    ///     bounded by <see cref="PermissionService.MaxHierarchyDepth"/>.</description></item>
    ///   <item><description>Any other scheme — returns <c>null</c> (no
    ///     permission). Future schemes that participate in authorisation
    ///     register their own branch.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="caller">The authenticated caller's address.</param>
    /// <param name="unitId">The unit's identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The effective <see cref="PermissionLevel"/>, or <c>null</c> when the caller has no direct or inherited permission.</returns>
    Task<PermissionLevel?> ResolveEffectivePermissionAsync(Address caller, Guid unitId, CancellationToken cancellationToken = default);
}
