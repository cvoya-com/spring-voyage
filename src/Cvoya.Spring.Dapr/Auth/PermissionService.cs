// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Units;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IPermissionService"/>. Resolves a caller's effective
/// <see cref="PermissionLevel"/> on a unit, branching on the caller's
/// <see cref="Address.Scheme"/>:
///
/// <list type="bullet">
///   <item><description><c>tenant-user://&lt;operator&gt;</c> — OSS implicit
///     <see cref="PermissionLevel.Owner"/> on every unit (#2768). The OSS
///     deployment ships with exactly one TenantUser; an explicit grant row
///     carries no information.</description></item>
///   <item><description><c>human://&lt;id&gt;</c> — direct/inherited lookup
///     against the <c>unit_human_permissions</c> EF table, walking ancestor
///     units via <see cref="IUnitHierarchyResolver"/> and consulting each
///     unit's <see cref="UnitPermissionInheritance"/> setting so opaque
///     sub-units block ancestor authority from cascading through them.</description></item>
///   <item><description>Any other scheme — <c>null</c> (no permission).</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Pre-#2768 this service accepted a string username and side-effect-upserted
/// a HumanEntity via <c>IHumanIdentityResolver.ResolveByUsernameAsync</c>
/// during the permission walk, which auto-minted a phantom "local-dev-user"
/// row on every OSS request and produced the conflation that #2766
/// surfaces. The Address-shaped contract removes the upsert: every caller
/// arrives with a typed scheme + Guid identity, and only humans key into
/// the grant table.
/// </remarks>
public class PermissionService(
    IUnitHumanPermissionStore permissionStore,
    IUnitHierarchyResolver hierarchyResolver,
    IUnitLiveConfigStore liveConfigStore,
    ILoggerFactory loggerFactory) : IPermissionService
{
    /// <summary>
    /// Matches <c>UnitMembershipCoordinator.MaxCycleDetectionDepth</c> so the
    /// permission walk agrees with the membership cycle detector on "maximum
    /// sensible nesting." Exceeding the bound stops the walk and returns
    /// whatever grant has been seen so far — pathological graphs never loop.
    /// </summary>
    internal const int MaxHierarchyDepth = UnitMembershipCoordinator.MaxCycleDetectionDepth;

    private readonly ILogger _logger = loggerFactory.CreateLogger<PermissionService>();

    /// <inheritdoc />
    public async Task<PermissionLevel?> ResolvePermissionAsync(
        Address caller,
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (IsTenantUserScheme(caller.Scheme))
        {
            // OSS implicit-owner rule (#2768): the single operator TenantUser
            // has Owner everywhere by construction. Direct and effective
            // resolve to the same value for the operator.
            return PermissionLevel.Owner;
        }

        if (!IsHumanScheme(caller.Scheme))
        {
            return null;
        }

        try
        {
            return await permissionStore.GetPermissionAsync(unitId, caller.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to resolve direct permission for {Caller} in unit {UnitId}",
                caller, unitId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<PermissionLevel?> ResolveEffectivePermissionAsync(
        Address caller,
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (IsTenantUserScheme(caller.Scheme))
        {
            // OSS implicit-owner rule (#2768): no walk needed — the operator
            // owns every unit in the tenant. Cloud overlays that wire a
            // per-tenant-user permission model replace this service via DI.
            return PermissionLevel.Owner;
        }

        if (!IsHumanScheme(caller.Scheme) || unitId == Guid.Empty)
        {
            return null;
        }

        var humanGuid = caller.Id;

        // Step 1: explicit grant on the target unit always wins. A direct
        // grant is authoritative — including a deliberate downgrade. The
        // #414 design rule is "direct beats inherited."
        PermissionLevel? direct;
        try
        {
            direct = await permissionStore.GetPermissionAsync(unitId, humanGuid, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Effective-permission walk: direct read failed for human {HumanId} in unit {UnitId}",
                humanGuid, unitId);
            return null;
        }

        if (direct.HasValue)
        {
            return direct;
        }

        // Step 2: walk ancestors, honouring the Isolated inheritance mode
        // on each hop. The walk visits nearest ancestor first; the first
        // direct grant found wins. Traversal stops when:
        //   * a unit has no parent (root);
        //   * an intermediate unit is marked Isolated — ancestor authority
        //     does not flow through an opaque permission boundary;
        //   * depth exceeds MaxHierarchyDepth — a pathological graph cannot
        //     silently promote a caller to admin;
        //   * a cycle is detected (defensive — membership should reject cycles
        //     on insertion, but a state-store anomaly must never loop us).
        var visited = new HashSet<Guid> { unitId };
        var current = new Address(Address.UnitScheme, unitId);
        var depth = 0;

        while (true)
        {
            if (depth >= MaxHierarchyDepth)
            {
                _logger.LogWarning(
                    "Effective-permission walk exceeded max depth {MaxDepth} for human {HumanId} starting at {UnitId}; stopping.",
                    MaxHierarchyDepth, humanGuid, unitId);
                return null;
            }

            IReadOnlyList<Address> parents;
            try
            {
                parents = await hierarchyResolver.GetParentsAsync(current, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Effective-permission walk: parent lookup failed at {Current} for human {HumanId}; stopping walk.",
                    current, humanGuid);
                return null;
            }

            if (parents.Count == 0)
            {
                // Reached a root — no more ancestors to consult.
                return null;
            }

            // A well-formed hierarchy has exactly one parent per unit
            // (#217). If a deployment has more than one, the contract is
            // "strongest grant wins" — evaluate them all.
            PermissionLevel? best = null;
            Address? nextCurrent = null;

            foreach (var parent in parents)
            {
                if (!visited.Add(parent.Id))
                {
                    continue;
                }

                // If the direction we're about to step from is marked
                // Isolated, ancestor authority is blocked. Check the
                // inheritance flag on the CURRENT unit (the child we're
                // stepping from) — that's the boundary the ancestor would
                // have to cross.
                var isolated = await GetInheritanceAsync(current, cancellationToken);
                if (isolated == UnitPermissionInheritance.Isolated)
                {
                    _logger.LogDebug(
                        "Effective-permission walk: unit {Current} is isolated; stopping ancestor walk for human {HumanId}.",
                        current, humanGuid);
                    return best;
                }

                PermissionLevel? grant;
                try
                {
                    grant = await permissionStore.GetPermissionAsync(parent.Id, humanGuid, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Effective-permission walk: direct read failed for human {HumanId} in ancestor {Parent}; continuing walk.",
                        humanGuid, parent);
                    continue;
                }

                if (grant.HasValue && (best is null || (int)grant.Value > (int)best.Value))
                {
                    best = grant;
                }

                if (nextCurrent is null)
                {
                    nextCurrent = parent;
                }
            }

            if (best.HasValue)
            {
                return best;
            }

            if (nextCurrent is null)
            {
                // Every parent was either already visited or unreadable —
                // nothing further to explore.
                return null;
            }

            current = nextCurrent;
            depth++;
        }
    }

    /// <summary>
    /// Reads the unit's <see cref="UnitPermissionInheritance"/> flag from
    /// the <c>unit_live_config</c> EF row via
    /// <see cref="IUnitLiveConfigStore"/>. ADR-0040 / #2049 places the
    /// flag in EF so the inheritance lookup is a SQL read with no
    /// cold-activation hop through the unit actor.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="UnitPermissionInheritance.Isolated"/> on every
    /// failure — the safe choice is to DENY inheritance when we cannot
    /// confirm the boundary is permissive. A permission service that silently
    /// assumed Inherit on failure would be a confused-deputy risk.
    /// </remarks>
    private async Task<UnitPermissionInheritance> GetInheritanceAsync(Address unit, CancellationToken ct)
    {
        try
        {
            return await liveConfigStore.GetPermissionInheritanceAsync(unit.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Effective-permission walk: could not read inheritance mode for {Unit}; treating as Isolated for safety.",
                unit);
            return UnitPermissionInheritance.Isolated;
        }
    }

    private static bool IsTenantUserScheme(string? scheme)
        => string.Equals(scheme, Address.TenantUserScheme, StringComparison.OrdinalIgnoreCase);

    private static bool IsHumanScheme(string? scheme)
        => string.Equals(scheme, Address.HumanScheme, StringComparison.OrdinalIgnoreCase);
}
