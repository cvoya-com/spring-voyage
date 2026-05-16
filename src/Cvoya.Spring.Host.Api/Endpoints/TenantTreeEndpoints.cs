// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Globalization;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// Maps the tenant-tree API surface introduced in SVR-tenant-tree (plan
/// §3 / §5). Exposes <c>GET /api/v1/tenant/tree</c> — a single-payload
/// snapshot of the tenant's units, agents, and multi-parent alias edges
/// that drives the canonical <c>/units</c> Explorer surface on the
/// frontend. Size budget is documented in <c>#815</c> §3 (≤500 nodes).
/// </summary>
public static class TenantTreeEndpoints
{
    /// <summary>Cache-Control window for the tree payload. Short enough to
    /// absorb Cmd-K + dashboard fanout, long enough to ride out the typical
    /// operator navigation bounce between Explorer tabs without re-fetching.</summary>
    // #1451: lowered from 15 → 1 so post-mutation reads (e.g. the
    // wizard's create-unit flow) see fresh data on the very next
    // explorer render. The 15 s window was generous for dashboard
    // fan-out but caused the browser to serve a stale cached tree
    // when the wizard navigated to `/units?node=<new-unit>` within
    // the same session. React Query's per-window cache still dedupes
    // fast back-to-back subscriptions; the HTTP cache is now a thin
    // burst-protection layer rather than a UX-affecting freshness gate.
    private const int CacheMaxAgeSeconds = 1;

    /// <summary>
    /// Registers the tenant-tree endpoint. Call from <c>Program.cs</c>
    /// alongside <c>MapDashboardEndpoints</c>. Returns a single
    /// <see cref="RouteGroupBuilder"/> so callers can apply
    /// <c>RequireAuthorization()</c> uniformly.
    /// </summary>
    public static RouteGroupBuilder MapTenantTreeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(string.Empty);

        group.MapGet("/api/v1/tenant/tree", GetTenantTreeAsync)
            .WithTags("Tenant")
            .WithName("GetTenantTree")
            .WithSummary("Synthesized tenant → units → agents tree with multi-parent alias edges")
            .Produces<TenantTreeResponse>(StatusCodes.Status200OK);

        return group;
    }

    private static async Task<IResult> GetTenantTreeAsync(
        HttpContext httpContext,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitMembershipRepository memberships,
        [FromServices] IUnitSubunitMembershipRepository subunitMemberships,
        [FromServices] ITenantContext tenantContext,
        [FromServices] ITenantRegistry tenantRegistry,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.TenantTreeEndpoints");
        var tenantId = tenantContext.CurrentTenantId;
        var entries = await directoryService.ListAllAsync(cancellationToken);

        // #1450: skip entries whose path is null/empty so a single
        // poisoned directory row (left behind by a partially-failed
        // register) can't take this endpoint down for the rest of the
        // process lifetime.
        var unitEntries = entries
            .Where(e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            .Where(e => !string.IsNullOrEmpty(e.Address.Path))
            .OrderBy(e => e.Address.Path, StringComparer.Ordinal)
            .ToList();

        var unitEntriesById = unitEntries.ToDictionary(
            e => e.Address.Path, StringComparer.Ordinal);

        var agentEntries = entries
            .Where(e => string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
            .Where(e => !string.IsNullOrEmpty(e.Address.Path))
            .ToDictionary(e => e.Address.Path, StringComparer.Ordinal);

        var allMemberships = await memberships.ListAllAsync(cancellationToken);

        // Build Guid → slug lookup maps from the already-loaded directory
        // entries. UnitMembership carries Guid ids; we resolve back to
        // slugs for tree building so the frontend-visible node ids remain
        // stable slug-based paths.
        var agentSlugByGuid = entries
            .Where(e => string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrEmpty(e.Address.Path))
            .ToDictionary(e => e.ActorId, e => e.Address.Path);

        var unitSlugByGuid = entries
            .Where(e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrEmpty(e.Address.Path))
            .ToDictionary(e => e.ActorId, e => e.Address.Path);

        var primaryByAgent = allMemberships
            .Where(m => m.IsPrimary
                     && agentSlugByGuid.ContainsKey(m.AgentId)
                     && unitSlugByGuid.ContainsKey(m.UnitId))
            .ToDictionary(
                m => agentSlugByGuid[m.AgentId],
                m => unitSlugByGuid[m.UnitId],
                StringComparer.Ordinal);

        var membershipsByUnit = allMemberships
            .Where(m => unitSlugByGuid.ContainsKey(m.UnitId))
            .GroupBy(m => unitSlugByGuid[m.UnitId], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // #1154 / #2052: pull the persistent sub-unit projection so the
        // tree can nest child units under their parent. Tenant-root
        // edges (parent_id == tenantId) are the explicit "this unit is
        // top-level" marker per ADR-0040 — they are NOT shown as parent
        // links in the tree but are used to identify which units render
        // directly under the tenant node. Filter out edges whose child
        // has no live directory entry — leftover ghosts that the
        // cascade hasn't caught up with would otherwise render as
        // broken nodes.
        var allSubunitEdges = await subunitMemberships.ListAllAsync(cancellationToken);
        var unitToUnitEdges = allSubunitEdges
            .Where(e => e.ParentUnitId != tenantId
                     && unitSlugByGuid.ContainsKey(e.ParentUnitId)
                     && unitSlugByGuid.ContainsKey(e.ChildUnitId))
            .ToList();

        var childUnitsByParent = unitToUnitEdges
            .GroupBy(e => unitSlugByGuid[e.ParentUnitId], StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g
                    .Select(e => unitSlugByGuid[e.ChildUnitId])
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        // #2052: top-level units are exactly those with an explicit
        // tenant-root edge. The previous "zero edges" heuristic is
        // gone. Units that have neither a tenant-root edge nor a
        // unit-parent edge are orphaned — render them under the tenant
        // as a fallback so they remain reachable while the operator
        // repairs the parent link.
        var topLevelUnitIds = allSubunitEdges
            .Where(e => e.ParentUnitId == tenantId
                     && unitSlugByGuid.ContainsKey(e.ChildUnitId))
            .Select(e => unitSlugByGuid[e.ChildUnitId])
            .ToHashSet(StringComparer.Ordinal);

        // Any unit that has a unit-parent edge is no longer a tenant-
        // root candidate — it renders under its parent rather than
        // alongside it.
        var nestedUnitIds = unitToUnitEdges
            .Select(e => unitSlugByGuid[e.ChildUnitId])
            .ToHashSet(StringComparer.Ordinal);

        // #1032: look up the real lifecycle status for each unit via its
        // actor. Previously every unit was pinned to "running", which left
        // operators looking at a green dot and the badge text "Running"
        // even for Draft units that can't accept dispatches. Dashboard
        // endpoints already pay this per-unit actor round-trip (see
        // DashboardEndpoints.GetUnitsSummaryAsync) and the cache-control
        // window on this endpoint (15s) absorbs the fanout.
        var unitStatuses = new Dictionary<string, LifecycleStatus>(StringComparer.Ordinal);
        foreach (var unit in unitEntries)
        {
            unitStatuses[unit.Address.Path] =
                await TryGetLifecycleStatusAsync(actorProxyFactory, Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unit.ActorId), logger, unit.Address.Path, cancellationToken);
        }

        // Walk the tree top-down from the units that render under the
        // tenant. Per #2052 a unit is rendered under the tenant when it
        // either has an explicit tenant-root edge OR is orphaned (no
        // parent edges at all — should not happen post-#2052 but is
        // tolerated as a fallback so an operator-triggered repair stays
        // possible). The visited set defends against a corrupted
        // projection — cycle prevention lives on the membership-coord
        // write path; we don't trust the projection blindly here.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var rootUnitNodes = unitEntries
            .Where(u => !nestedUnitIds.Contains(u.Address.Path))
            .Select(u => BuildUnitNode(
                u,
                unitEntriesById,
                unitStatuses,
                membershipsByUnit,
                childUnitsByParent,
                agentEntries,
                primaryByAgent,
                agentSlugByGuid,
                visited,
                logger))
            .ToList();

        var tenantIdString = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(tenantId);
        var tenantRecord = await tenantRegistry.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var tenantName = tenantRecord?.DisplayName ?? tenantIdString;
        var tenantNode = new TenantTreeNode(
            Id: $"tenant://{tenantIdString}",
            Name: tenantName,
            Kind: "Tenant",
            Status: "running",
            Children: rootUnitNodes);

        httpContext.Response.Headers.CacheControl =
            $"private, max-age={CacheMaxAgeSeconds.ToString(CultureInfo.InvariantCulture)}";

        return Results.Ok(new TenantTreeResponse(tenantNode));
    }

    private static TenantTreeNode BuildUnitNode(
        DirectoryEntry unit,
        IReadOnlyDictionary<string, DirectoryEntry> unitEntriesById,
        IReadOnlyDictionary<string, LifecycleStatus> unitStatuses,
        IReadOnlyDictionary<string, List<UnitMembership>> membershipsByUnit,
        IReadOnlyDictionary<string, IReadOnlyList<string>> childUnitsByParent,
        IReadOnlyDictionary<string, DirectoryEntry> agentEntries,
        IReadOnlyDictionary<string, string> primaryByAgent,
        IReadOnlyDictionary<Guid, string> agentSlugByGuid,
        HashSet<string> visited,
        ILogger logger)
    {
        var unitPath = unit.Address.Path;
        var status = unitStatuses.TryGetValue(unitPath, out var persisted)
            ? persisted
            : LifecycleStatus.Draft;
        var displayName = string.IsNullOrWhiteSpace(unit.DisplayName) ? unitPath : unit.DisplayName;
        var description = string.IsNullOrWhiteSpace(unit.Description) ? null : unit.Description;

        // Defense in depth against a corrupted projection: a cycle
        // would otherwise blow the stack here. Cycle prevention is
        // enforced on the actor write path; this guard renders the
        // duplicate node as a leaf and logs once so operators can spot
        // the drift.
        if (!visited.Add(unitPath))
        {
            logger.LogWarning(
                "Tenant tree: skipping duplicate unit {UnitPath} discovered via sub-unit projection (possible cycle).",
                unitPath);
            return new TenantTreeNode(
                Id: unitPath,
                Name: displayName,
                Kind: "Unit",
                Status: ToWireStatus(status),
                Desc: description,
                Children: Array.Empty<TenantTreeNode>(),
                DefinitionId: unit.ActorId);
        }

        var rows = membershipsByUnit.TryGetValue(unitPath, out var list)
            ? list
            : new List<UnitMembership>();

        var agentNodes = rows
            .Where(m => m.Enabled)
            .Select(m => BuildAgentNode(m, agentEntries, primaryByAgent, agentSlugByGuid))
            .Where(n => n is not null)
            .Cast<TenantTreeNode>()
            .ToList();

        // Sub-unit children sit alongside agent children. Order is
        // deterministic (sub-units first, alpha; then agents in
        // membership order) so the Explorer's expand/collapse state and
        // `findIndex` stay stable across reloads.
        var childUnitNodes = childUnitsByParent.TryGetValue(unitPath, out var childIds)
            ? childIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .Where(unitEntriesById.ContainsKey)
                .Select(id => BuildUnitNode(
                    unitEntriesById[id],
                    unitEntriesById,
                    unitStatuses,
                    membershipsByUnit,
                    childUnitsByParent,
                    agentEntries,
                    primaryByAgent,
                    agentSlugByGuid,
                    visited,
                    logger))
                .ToList()
            : new List<TenantTreeNode>();

        var allChildren = new List<TenantTreeNode>(childUnitNodes.Count + agentNodes.Count);
        allChildren.AddRange(childUnitNodes);
        allChildren.AddRange(agentNodes);

        return new TenantTreeNode(
            Id: unitPath,
            Name: displayName,
            Kind: "Unit",
            Status: ToWireStatus(status),
            Desc: description,
            Children: allChildren,
            DefinitionId: unit.ActorId);
    }

    /// <summary>
    /// Read a unit's persisted status from its actor. Mirrors the fallback
    /// policy in <see cref="DashboardEndpoints.GetUnitsSummaryAsync"/>: a
    /// missing or unreachable actor collapses to <see cref="LifecycleStatus.Draft"/>
    /// so the tree still renders rather than failing the whole fetch.
    /// </summary>
    private static async Task<LifecycleStatus> TryGetLifecycleStatusAsync(
        IActorProxyFactory actorProxyFactory,
        string actorId,
        ILogger logger,
        string unitPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(actorId), nameof(UnitActor));
            return await proxy.GetStatusAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to read persisted status for unit {UnitPath}; reporting Draft in tenant tree.",
                unitPath);
            return LifecycleStatus.Draft;
        }
    }

    /// <summary>
    /// Maps the <see cref="LifecycleStatus"/> lifecycle enum to the lowercase
    /// wire vocabulary consumed by <c>src/lib/api/validate-tenant-tree.ts</c>
    /// on the portal. Kept next to the unit-node builder so a new enum
    /// value fails the wire-status switch fast instead of silently leaking
    /// into the tree as <c>stopped</c>.
    /// </summary>
    private static string ToWireStatus(LifecycleStatus status) => status switch
    {
        LifecycleStatus.Draft => "draft",
        LifecycleStatus.Stopped => "stopped",
        LifecycleStatus.Starting => "starting",
        LifecycleStatus.Running => "running",
        LifecycleStatus.Stopping => "stopping",
        LifecycleStatus.Error => "error",
        LifecycleStatus.Validating => "validating",
        _ => "stopped",
    };

    private static TenantTreeNode? BuildAgentNode(
        UnitMembership membership,
        IReadOnlyDictionary<string, DirectoryEntry> agentEntries,
        IReadOnlyDictionary<string, string> primaryByAgent,
        IReadOnlyDictionary<Guid, string> agentSlugByGuid)
    {
        // Resolve the agent Guid to its slug so we can look up the directory
        // entry. An agent might have a membership row but no directory entry
        // (transient during registration). Skip it rather than render a
        // half-typed node; the next fetch will pick it up.
        if (!agentSlugByGuid.TryGetValue(membership.AgentId, out var agentSlug))
        {
            return null;
        }

        if (!agentEntries.TryGetValue(agentSlug, out var agent))
        {
            return null;
        }

        primaryByAgent.TryGetValue(agentSlug, out var primary);

        return new TenantTreeNode(
            Id: agent.Address.Path,
            Name: string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.Address.Path : agent.DisplayName,
            Kind: "Agent",
            Status: "running",
            Desc: string.IsNullOrWhiteSpace(agent.Description) ? null : agent.Description,
            Role: agent.Role,
            PrimaryParentId: primary,
            DefinitionId: agent.Address.Id);
    }
}
