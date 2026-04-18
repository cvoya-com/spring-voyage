// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

/// <summary>
/// Controls whether a unit inherits human permission grants from its ancestor
/// units. Persisted on the unit actor under
/// <see cref="Cvoya.Spring.Dapr.Actors.StateKeys.UnitPermissionInheritance"/>;
/// consulted by <see cref="IPermissionService.ResolveEffectivePermissionAsync"/>
/// (#414) when walking up the parent chain to compute an effective
/// permission level.
/// </summary>
/// <remarks>
/// <para>
/// The default is <see cref="Inherit"/> — a human who is an Owner or Operator
/// on a parent unit is treated as having at least that permission on every
/// descendant unit unless something along the path blocks the walk. This
/// matches the boundary model in
/// <see cref="Cvoya.Spring.Core.Capabilities.UnitBoundary"/>: a parent
/// transparently "contains" its children, so administrative authority flows
/// down by default.
/// </para>
/// <para>
/// Setting the unit to <see cref="Isolated"/> is the permission-layer
/// analogue of an opaque boundary: ancestor grants stop at the isolated
/// unit. Direct grants on the unit itself (or on its own descendants) still
/// apply — isolation blocks inheritance, it does not revoke existing
/// permissions.
/// </para>
/// <para>
/// Explicit grants on a child unit always override ancestor grants: a child
/// that grants a human <see cref="PermissionLevel.Viewer"/> is not silently
/// promoted to Owner because the parent happens to grant Owner. Explicit
/// is explicit.
/// </para>
/// </remarks>
public enum UnitPermissionInheritance
{
    /// <summary>
    /// Default. Permissions granted on any ancestor unit flow down to this
    /// unit. The effective permission for a human on this unit is the
    /// strongest direct grant in the unit itself, or — if no direct grant
    /// exists — the strongest ancestor grant.
    /// </summary>
    Inherit = 0,

    /// <summary>
    /// The unit does not inherit ancestor grants. Only direct permission
    /// entries on this unit (and grants on its own descendants, via the same
    /// recursive rule) apply. Equivalent to an opaque boundary from the
    /// permission layer's perspective — used for isolated tenants, sandboxed
    /// sub-teams, or any unit that must not be administrable through a
    /// parent-scoped role.
    /// </summary>
    Isolated = 1,
}