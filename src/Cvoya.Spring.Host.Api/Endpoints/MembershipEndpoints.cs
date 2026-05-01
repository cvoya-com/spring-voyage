// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps the unit-membership API surface introduced in #160 / C2b-1:
/// <c>GET /api/v1/agents/{id}/memberships</c>,
/// <c>GET /api/v1/units/{id}/memberships</c>,
/// <c>PUT /api/v1/units/{unitId}/memberships/{agentAddress}</c>, and
/// <c>DELETE /api/v1/units/{unitId}/memberships/{agentAddress}</c>.
/// </summary>
/// <remarks>
/// URL path parameters remain slug-shaped for human-friendly URLs. Each
/// handler resolves the slug to a stable UUID (actor ID) via
/// <see cref="IDirectoryService"/> at the boundary and passes the UUID
/// downstream so the underlying storage is identity-stable (#1492).
/// Per-membership config overrides (<c>model</c>, <c>specialty</c>,
/// <c>enabled</c>, <c>executionMode</c>) are PERSISTED here but NOT YET
/// CONSULTED at dispatch time. Receive-path consumption lands in the
/// follow-on C2b-2 work.
/// </remarks>
public static class MembershipEndpoints
{
    /// <summary>
    /// Registers all membership endpoints at the top-level route builder.
    /// Call from <c>Program.cs</c> alongside <c>MapAgentEndpoints</c> and
    /// <c>MapUnitEndpoints</c>. Returns a single
    /// <see cref="RouteGroupBuilder"/> with no common prefix so callers
    /// can apply <c>RequireAuthorization()</c> uniformly.
    /// </summary>
    public static RouteGroupBuilder MapMembershipEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(string.Empty);

        group.MapGet("/api/v1/tenant/agents/{id}/memberships", ListAgentMembershipsAsync)
            .WithTags("Agents")
            .WithName("ListAgentMemberships")
            .WithSummary("List every unit this agent belongs to, with per-membership config overrides")
            .Produces<UnitMembershipResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/api/v1/tenant/units/{id}/memberships", ListUnitMembershipsAsync)
            .WithTags("Units")
            .WithName("ListUnitMemberships")
            .WithSummary("List every agent that is a member of this unit, with per-membership config overrides")
            .Produces<UnitMembershipResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/api/v1/tenant/units/{unitId}/memberships/{agentAddress}", UpsertMembershipAsync)
            .WithTags("Units")
            .WithName("UpsertUnitMembership")
            .WithSummary("Create or update the per-membership config overrides for an agent in this unit")
            .Produces<UnitMembershipResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/api/v1/tenant/units/{unitId}/memberships/{agentAddress}", DeleteMembershipAsync)
            .WithTags("Units")
            .WithName("DeleteUnitMembership")
            .WithSummary("Remove an agent's membership of this unit")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    private static async Task<IResult> ListAgentMembershipsAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitMembershipRepository repository,
        CancellationToken cancellationToken)
    {
        var address = new Address("agent", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Agent '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Resolve slug → UUID at the boundary (#1492).
        if (!Guid.TryParse(entry.ActorId, out var agentUuid))
        {
            return Results.Problem(
                detail: $"Agent '{id}' has no stable UUID identity.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var memberships = await repository.ListByAgentAsync(agentUuid, cancellationToken);
        var unitActorIdMap = await ResolveUnitActorIdsAsync(memberships, directoryService, cancellationToken);
        var agentActorIdMap = await ResolveAgentActorIdsAsync(memberships, directoryService, cancellationToken);
        return Results.Ok(memberships.Select(m => ToResponse(m, unitActorIdMap, agentActorIdMap, entry)).ToArray());
    }

    private static async Task<IResult> ListUnitMembershipsAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitMembershipRepository repository,
        CancellationToken cancellationToken)
    {
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Resolve slug → UUID at the boundary (#1492).
        if (!Guid.TryParse(entry.ActorId, out var unitUuid))
        {
            return Results.Problem(
                detail: $"Unit '{id}' has no stable UUID identity.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var memberships = await repository.ListByUnitAsync(unitUuid, cancellationToken);
        var unitActorIdMap = new Dictionary<Guid, DirectoryEntry> { [unitUuid] = entry };
        var agentActorIdMap = await ResolveAgentActorIdsAsync(memberships, directoryService, cancellationToken);
        return Results.Ok(memberships.Select(m => ToResponse(m, unitActorIdMap, agentActorIdMap, null)).ToArray());
    }

    private static async Task<IResult> UpsertMembershipAsync(
        string unitId,
        string agentAddress,
        UpsertMembershipRequest request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitMembershipRepository repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(unitId) || string.IsNullOrWhiteSpace(agentAddress))
        {
            return Results.Problem(
                detail: "unitId and agentAddress are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var unitEntry = await directoryService.ResolveAsync(new Address("unit", unitId), cancellationToken);
        if (unitEntry is null)
        {
            return Results.Problem(
                detail: $"Unit '{unitId}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var agentEntry = await directoryService.ResolveAsync(new Address("agent", agentAddress), cancellationToken);
        if (agentEntry is null)
        {
            return Results.Problem(
                detail: $"Agent '{agentAddress}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Resolve slugs → UUIDs at the boundary (#1492).
        if (!Guid.TryParse(unitEntry.ActorId, out var unitUuid))
        {
            return Results.Problem(
                detail: $"Unit '{unitId}' has no stable UUID identity.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Guid.TryParse(agentEntry.ActorId, out var agentUuid))
        {
            return Results.Problem(
                detail: $"Agent '{agentAddress}' has no stable UUID identity.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var membership = new UnitMembership(
            UnitId: unitUuid,
            AgentId: agentUuid,
            Model: request.Model,
            Specialty: request.Specialty,
            Enabled: request.Enabled ?? true,
            ExecutionMode: request.ExecutionMode);

        await repository.UpsertAsync(membership, cancellationToken);

        var persisted = await repository.GetAsync(unitUuid, agentUuid, cancellationToken);
        var row = persisted ?? membership;
        var unitActorIdMap = new Dictionary<Guid, DirectoryEntry> { [unitUuid] = unitEntry };
        var agentActorIdMap = new Dictionary<Guid, DirectoryEntry> { [agentUuid] = agentEntry };

        return Results.Ok(ToResponse(row, unitActorIdMap, agentActorIdMap, agentEntry));
    }

    private static async Task<IResult> DeleteMembershipAsync(
        string unitId,
        string agentAddress,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitMembershipRepository repository,
        CancellationToken cancellationToken)
    {
        // Resolve slugs → UUIDs at the boundary (#1492).
        var unitEntry = await directoryService.ResolveAsync(new Address("unit", unitId), cancellationToken);
        if (unitEntry is null || !Guid.TryParse(unitEntry.ActorId, out var unitUuid))
        {
            return Results.Problem(
                detail: $"Unit '{unitId}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var agentEntry = await directoryService.ResolveAsync(new Address("agent", agentAddress), cancellationToken);
        if (agentEntry is null || !Guid.TryParse(agentEntry.ActorId, out var agentUuid))
        {
            return Results.Problem(
                detail: $"Agent '{agentAddress}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var existing = await repository.GetAsync(unitUuid, agentUuid, cancellationToken);
        if (existing is null)
        {
            return Results.Problem(
                detail: $"No membership exists for agent '{agentAddress}' in unit '{unitId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            await repository.DeleteAsync(unitUuid, agentUuid, cancellationToken);
        }
        catch (AgentMembershipRequiredException ex)
        {
            // #744: removing the last membership would orphan the agent.
            // Surface as 409 Conflict per the ProblemDetails shape
            // established by #192 for rejected state changes.
            return Results.Problem(
                title: "Agent must belong to at least one unit",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["agentId"] = ex.AgentId,
                    ["unitId"] = ex.UnitId,
                });
        }
        return Results.NoContent();
    }

    /// <summary>
    /// Batch-resolves unit directory entries for all distinct unit UUIDs in
    /// <paramref name="memberships"/>. Missing or failed lookups are omitted
    /// from the map — <see cref="ToResponse"/> falls back to the UUID string.
    /// </summary>
    private static async Task<Dictionary<Guid, DirectoryEntry>> ResolveUnitActorIdsAsync(
        IReadOnlyList<UnitMembership> memberships,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        // Use ListAll for a single round-trip when resolving multiple units.
        // The directory warms its in-memory cache on the first ListAll call,
        // so subsequent single-unit resolves are cache hits.
        var distinctUnitIds = memberships
            .Select(m => m.UnitId)
            .Distinct()
            .ToHashSet();

        if (distinctUnitIds.Count == 0)
        {
            return [];
        }

        var map = new Dictionary<Guid, DirectoryEntry>();
        var allEntries = await directoryService.ListAllAsync(cancellationToken);
        foreach (var entry in allEntries)
        {
            if (!string.Equals(entry.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Guid.TryParse(entry.ActorId, out var uuid) && distinctUnitIds.Contains(uuid))
            {
                map[uuid] = entry;
            }
        }

        return map;
    }

    /// <summary>
    /// Batch-resolves agent directory entries for all distinct agent UUIDs in
    /// <paramref name="memberships"/>. Missing or failed lookups are omitted
    /// from the map — <see cref="ToResponse"/> falls back to the UUID string.
    /// </summary>
    private static async Task<Dictionary<Guid, DirectoryEntry>> ResolveAgentActorIdsAsync(
        IReadOnlyList<UnitMembership> memberships,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var distinctAgentIds = memberships
            .Select(m => m.AgentId)
            .Distinct()
            .ToHashSet();

        if (distinctAgentIds.Count == 0)
        {
            return [];
        }

        var map = new Dictionary<Guid, DirectoryEntry>();
        var allEntries = await directoryService.ListAllAsync(cancellationToken);
        foreach (var entry in allEntries)
        {
            if (!string.Equals(entry.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Guid.TryParse(entry.ActorId, out var uuid) && distinctAgentIds.Contains(uuid))
            {
                map[uuid] = entry;
            }
        }

        return map;
    }

    /// <summary>
    /// Projects a <see cref="UnitMembership"/> row into its wire representation.
    ///
    /// Wire shape (#1492):
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>UnitId</c> carries the unit's identity-form address
    ///     <c>unit:id:&lt;uuid&gt;</c> so consumers get the stable, unambiguous
    ///     UUID — not a slug that could be reused after a delete/recreate.
    ///   </description></item>
    ///   <item><description>
    ///     <c>AgentAddress</c> carries the agent's slug-form path (e.g. "ada")
    ///     for backward-compat URL routing (portal uses this as a URL segment).
    ///     The <c>Member</c> field carries the agent's identity-form address
    ///     <c>agent:id:&lt;uuid&gt;</c> — the two together avoid duplication
    ///     while preserving the routing convenience (see #1492 design note).
    ///   </description></item>
    /// </list>
    /// </summary>
    internal static UnitMembershipResponse ToResponse(
        UnitMembership m,
        IReadOnlyDictionary<Guid, DirectoryEntry>? unitActorIdMap = null,
        IReadOnlyDictionary<Guid, DirectoryEntry>? agentActorIdMap = null,
        DirectoryEntry? agentEntryHint = null)
    {
        // Unit identity: emit unit:id:<uuid> form.
        var unitAddress = Address.ForIdentity(Address.UnitScheme, m.UnitId).ToIdentityUri();

        // Agent slug for URL routing (agentAddress field stays slug-shaped).
        string agentSlug;
        if (agentActorIdMap is not null && agentActorIdMap.TryGetValue(m.AgentId, out var agentEntry))
        {
            agentSlug = agentEntry.Address.Path;
        }
        else if (agentEntryHint is not null)
        {
            agentSlug = agentEntryHint.Address.Path;
        }
        else
        {
            // Fallback: emit the UUID string so the field is never empty.
            agentSlug = m.AgentId.ToString();
        }

        // Member field: identity-form agent:id:<uuid>.
        var member = Address.ForIdentity(Address.AgentScheme, m.AgentId).ToIdentityUri();

        return new UnitMembershipResponse(
            unitAddress,
            agentSlug,
            member,
            m.Model,
            m.Specialty,
            m.Enabled,
            m.ExecutionMode,
            m.CreatedAt,
            m.UpdatedAt,
            m.IsPrimary);
    }
}