// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IPermissionService"/>. Resolves (humanId, unitId) pairs
/// by querying the unit actor's human-permission state. Implements the
/// hierarchy-aware resolver behind <see cref="ResolveEffectivePermissionAsync"/>
/// (#414) by walking parent units via <see cref="IUnitHierarchyResolver"/>
/// and consulting each unit's
/// <see cref="UnitPermissionInheritance"/> setting so opaque sub-units block
/// ancestor authority from cascading through them.
/// </summary>
public class PermissionService(
    IActorProxyFactory actorProxyFactory,
    IUnitHierarchyResolver hierarchyResolver,
    ILoggerFactory loggerFactory) : IPermissionService
{
    /// <summary>
    /// Matches <c>UnitActor.MaxCycleDetectionDepth</c> so the permission walk
    /// agrees with the membership cycle detector on "maximum sensible
    /// nesting." Exceeding the bound stops the walk and returns whatever
    /// grant has been seen so far — pathological graphs never loop.
    /// </summary>
    internal const int MaxHierarchyDepth = UnitActor.MaxCycleDetectionDepth;

    private readonly ILogger _logger = loggerFactory.CreateLogger<PermissionService>();

    /// <inheritdoc />
    public async Task<PermissionLevel?> ResolvePermissionAsync(
        string humanId,
        string unitId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(unitId), nameof(UnitActor));

            return await unitProxy.GetHumanPermissionAsync(humanId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to resolve direct permission for human {HumanId} in unit {UnitId}",
                humanId, unitId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<PermissionLevel?> ResolveEffectivePermissionAsync(
        string humanId,
        string unitId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(humanId) || string.IsNullOrEmpty(unitId))
        {
            return null;
        }

        // Step 1: explicit grant on the target unit always wins. A direct
        // grant is authoritative — including a deliberate downgrade. The
        // #414 design rule is "direct beats inherited."
        PermissionLevel? direct;
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(unitId), nameof(UnitActor));
            direct = await proxy.GetHumanPermissionAsync(humanId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Effective-permission walk: direct read failed for human {HumanId} in unit {UnitId}",
                humanId, unitId);
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
        var visited = new HashSet<string>(StringComparer.Ordinal) { unitId };
        var current = new Address("unit", unitId);
        var depth = 0;

        while (true)
        {
            if (depth >= MaxHierarchyDepth)
            {
                _logger.LogWarning(
                    "Effective-permission walk exceeded max depth {MaxDepth} for human {HumanId} starting at {UnitId}; stopping.",
                    MaxHierarchyDepth, humanId, unitId);
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
                    current, humanId);
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
                if (!visited.Add(ToKey(parent)))
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
                        current, humanId);
                    return best;
                }

                PermissionLevel? grant;
                try
                {
                    var parentProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                        new ActorId(parent.Path), nameof(UnitActor));
                    grant = await parentProxy.GetHumanPermissionAsync(humanId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Effective-permission walk: direct read failed for human {HumanId} in ancestor {Parent}; continuing walk.",
                        humanId, parent);
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

    private async Task<UnitPermissionInheritance> GetInheritanceAsync(Address unit, CancellationToken ct)
    {
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(unit.Path), nameof(UnitActor));
            return await proxy.GetPermissionInheritanceAsync(ct);
        }
        catch (Exception ex)
        {
            // If we cannot determine the flag, default to Isolated — the
            // safe choice is to DENY inheritance when we cannot confirm the
            // boundary is permissive. A permission service that silently
            // assumed Inherit on failure would be a confused-deputy risk.
            _logger.LogWarning(ex,
                "Effective-permission walk: could not read inheritance mode for {Unit}; treating as Isolated for safety.",
                unit);
            return UnitPermissionInheritance.Isolated;
        }
    }

    private static string ToKey(Address address) => $"{address.Scheme}://{address.Path}";
}