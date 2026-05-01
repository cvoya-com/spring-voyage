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

        var memberships = await repository.ListByAgentAsync(address.Path, cancellationToken);
        var actorIdMap = await ResolveAgentActorIdsAsync(memberships, directoryService, cancellationToken);
        return Results.Ok(memberships.Select(m => ToResponse(m, actorIdMap)).ToArray());
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

        var memberships = await repository.ListByUnitAsync(id, cancellationToken);
        var actorIdMap = await ResolveAgentActorIdsAsync(memberships, directoryService, cancellationToken);
        return Results.Ok(memberships.Select(m => ToResponse(m, actorIdMap)).ToArray());
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

        var membership = new UnitMembership(
            UnitId: unitId,
            AgentAddress: agentAddress,
            Model: request.Model,
            Specialty: request.Specialty,
            Enabled: request.Enabled ?? true,
            ExecutionMode: request.ExecutionMode);

        await repository.UpsertAsync(membership, cancellationToken);

        var persisted = await repository.GetAsync(unitId, agentAddress, cancellationToken);
        var row = persisted ?? membership;
        var actorId = agentEntry.ActorId;
        var actorIdMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [agentAddress] = actorId,
        };

        // persisted cannot be null here — we just wrote the row — but guard for safety.
        return Results.Ok(ToResponse(row, actorIdMap));
    }

    private static async Task<IResult> DeleteMembershipAsync(
        string unitId,
        string agentAddress,
        [FromServices] IUnitMembershipRepository repository,
        CancellationToken cancellationToken)
    {
        var existing = await repository.GetAsync(unitId, agentAddress, cancellationToken);
        if (existing is null)
        {
            return Results.Problem(
                detail: $"No membership exists for agent '{agentAddress}' in unit '{unitId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            await repository.DeleteAsync(unitId, agentAddress, cancellationToken);
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
                    ["agentAddress"] = ex.AgentAddress,
                    ["unitId"] = ex.UnitId,
                });
        }
        return Results.NoContent();
    }

    /// <summary>
    /// Batch-resolves actor IDs for all distinct agent addresses in
    /// <paramref name="memberships"/>. Missing or failed lookups fall back
    /// to the <c>AgentAddress</c> slug so the projection always completes.
    /// </summary>
    private static async Task<Dictionary<string, string>> ResolveAgentActorIdsAsync(
        IReadOnlyList<UnitMembership> memberships,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var distinct = memberships
            .Select(m => m.AgentAddress)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var agentAddress in distinct)
        {
            try
            {
                var entry = await directoryService.ResolveAsync(
                    new Address("agent", agentAddress), cancellationToken);
                map[agentAddress] = entry?.ActorId ?? agentAddress;
            }
            catch
            {
                // Directory failures must not break the listing.
                map[agentAddress] = agentAddress;
            }
        }

        return map;
    }

    /// <summary>
    /// Projects a <see cref="UnitMembership"/> row into its wire representation.
    /// The <c>Member</c> field is emitted as <c>agent:id:&lt;actorId&gt;</c>
    /// (the identity form) so consumers get an unambiguous stable identifier
    /// that is not confused with a slug-shaped actor id (#1490).
    /// The <paramref name="actorIdMap"/> provides the resolved actor UUID for
    /// the agent; when absent the agent's <c>AgentAddress</c> slug is used
    /// as a safe fallback so the projection always completes.
    /// </summary>
    internal static UnitMembershipResponse ToResponse(
        UnitMembership m,
        IReadOnlyDictionary<string, string>? actorIdMap = null)
    {
        // Prefer the resolved actor UUID; fall back to the slug stored in the row.
        var actorId = actorIdMap is not null && actorIdMap.TryGetValue(m.AgentAddress, out var id)
            ? id
            : m.AgentAddress;

        string member;
        if (Guid.TryParse(actorId, out var guid))
        {
            // Resolved to a proper UUID — emit the unambiguous identity form.
            member = Address.ForIdentity(Address.AgentScheme, guid).ToIdentityUri();
        }
        else
        {
            // Fallback: the actor id is slug-shaped (dev / test scenarios
            // without a real directory). Keep the navigation form so the
            // field is still useful.
            member = Address.ForAgent(actorId).ToCanonicalUri();
        }

        return new UnitMembershipResponse(
            m.UnitId,
            m.AgentAddress,
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