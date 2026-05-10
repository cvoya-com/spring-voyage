// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Units;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IPermissionService"/>. Resolves (humanId, unitId) pairs
/// by querying the <c>unit_human_permissions</c> EF table directly (#2044 /
/// ADR-0040). Implements the hierarchy-aware resolver behind
/// <see cref="ResolveEffectivePermissionAsync"/> (#414) by walking parent
/// units via <see cref="IUnitHierarchyResolver"/> and consulting each unit's
/// <see cref="UnitPermissionInheritance"/> setting so opaque sub-units block
/// ancestor authority from cascading through them.
/// </summary>
/// <remarks>
/// Pre-#2044 the service held an <see cref="IActorProxyFactory"/> and read
/// every grant through a unit-actor proxy, cold-activating each ancestor on
/// the walk. Post-#2044 grants are EF rows, so the resolution is a single
/// indexed SQL read per hop and the actor proxy is no longer required for
/// the grant itself. The proxy is still consulted for the
/// <see cref="UnitPermissionInheritance"/> flag — that key remains in actor
/// state for v0.1 and moves to <c>unit_live_config</c> in
/// <see href="https://github.com/cvoya-com/spring-voyage/issues/2049">#2049</see>.
///
/// <para>
/// #1491: The <paramref name="scopeFactory"/> resolves a scoped
/// <see cref="IHumanIdentityResolver"/> per call to convert the incoming
/// username string (from <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>)
/// into a stable UUID before querying the unit's permission row.
/// </para>
/// </remarks>
public class PermissionService(
    IUnitHumanPermissionStore permissionStore,
    IUnitHierarchyResolver hierarchyResolver,
    IActorProxyFactory inheritanceActorProxyFactory,
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory) : IPermissionService
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
        string humanId,
        string unitId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var humanGuid = await ResolveHumanGuidAsync(humanId, cancellationToken);
            if (humanGuid == Guid.Empty)
            {
                return null;
            }

            if (!TryParseUnitGuid(unitId, out var unitGuid))
            {
                return null;
            }

            return await permissionStore.GetPermissionAsync(unitGuid, humanGuid, cancellationToken);
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

        var humanGuid = await ResolveHumanGuidAsync(humanId, cancellationToken);
        if (humanGuid == Guid.Empty)
        {
            return null;
        }

        if (!TryParseUnitGuid(unitId, out var unitGuid))
        {
            // The route-level id did not parse as a Guid — the unit does
            // not exist in this deployment. Match the pre-#2044 directory-
            // miss behaviour: surface as "no permission" rather than
            // throwing, so a stale URL never leaks a 500 to the caller.
            return null;
        }

        // Step 1: explicit grant on the target unit always wins. A direct
        // grant is authoritative — including a deliberate downgrade. The
        // #414 design rule is "direct beats inherited."
        PermissionLevel? direct;
        try
        {
            direct = await permissionStore.GetPermissionAsync(unitGuid, humanGuid, cancellationToken);
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
        var current = new Address("unit", unitGuid);
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
                    grant = await permissionStore.GetPermissionAsync(parent.Id, humanGuid, cancellationToken);
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

    /// <summary>
    /// Converts the incoming human identity string to a UUID. Creates a
    /// short-lived scope to resolve a scoped <see cref="IHumanIdentityResolver"/>
    /// and calls <c>ResolveByUsernameAsync</c> (upsert on first-contact).
    /// Returns <see cref="Guid.Empty"/> when the resolver cannot map the
    /// caller — surface as "no permission" rather than throw, matching the
    /// pre-#2044 directory-miss behaviour.
    /// </summary>
    private async Task<Guid> ResolveHumanGuidAsync(
        string humanId,
        CancellationToken cancellationToken)
    {
        // #1695: identity-form callers (human:id:<uuid>) hand the GUID-hex
        // through this seam directly. Without this guard the next branch
        // calls ResolveByUsernameAsync(<guid-hex>) which doesn't match the
        // canonical username row (e.g. "local-dev-user"), and the
        // resolver's upsert-on-miss path creates a phantom humans row
        // keyed by the GUID-hex. The phantom's distinct UUID never
        // matches the unit's permission map → 403, plus a leaking row.
        // Recognise the format and short-circuit so the lookup goes
        // straight to the directory's id.
        if (Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(humanId, out var identityFormGuid))
        {
            return identityFormGuid;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var resolver = scope.ServiceProvider.GetRequiredService<IHumanIdentityResolver>();
            return await resolver.ResolveByUsernameAsync(humanId, null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not resolve human UUID for username {HumanId}; treating as no permission.",
                humanId);
            return Guid.Empty;
        }
    }

    /// <summary>
    /// Reads the unit's <see cref="UnitPermissionInheritance"/> flag through
    /// the unit-actor proxy. v0.1 keeps this read on actor state per ADR-0040;
    /// <see href="https://github.com/cvoya-com/spring-voyage/issues/2049">#2049</see>
    /// moves it to <c>unit_live_config</c> so the inheritance flag joins the
    /// SQL read path with the grant.
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
            var actorIdString = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unit.Id);
            var proxy = inheritanceActorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(actorIdString), nameof(UnitActor));
            return await proxy.GetPermissionInheritanceAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Effective-permission walk: could not read inheritance mode for {Unit}; treating as Isolated for safety.",
                unit);
            return UnitPermissionInheritance.Isolated;
        }
    }

    private static bool TryParseUnitGuid(string unitId, out Guid unitGuid)
        => Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitId, out unitGuid);

    private static string ToKey(Address address) => $"{address.Scheme}://{address.Path}";
}
