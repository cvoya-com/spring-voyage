// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// REST surface for the unit team-role membership table (#2409 /
/// ADR-0044 § 3). Mounted under
/// <c>/api/v1/tenant/units/{id}/members/humans</c> as a sibling to the
/// existing <c>{id}/humans/{humanId}/permissions</c> ACL surface — the
/// two are deliberately separate because ADR-0044 splits the package-
/// authored "who is on the team" facts from the operator-authored ACL
/// grants.
/// </summary>
/// <remarks>
/// <para>
/// Authorisation mirrors the per-unit human-permission routes in
/// <see cref="UnitEndpoints"/>: writes require an
/// <see cref="PermissionLevel.Owner"/> on the unit, reads require
/// <see cref="PermissionLevel.Viewer"/>. The
/// <see cref="UnitPermissionCheck"/> helper applies the
/// existence-first ordering so an unknown unit surfaces as 404 even when
/// the caller would not have been authorised on a hypothetical unit
/// with the same id (the #1029 contract).
/// </para>
/// <para>
/// The POST endpoint is idempotent on the natural key
/// <c>(unit, human, role)</c> per the ADR-0044 § 3 set-semantic
/// invariant. Re-posting the same tuple updates
/// <see cref="UnitMembershipHumanEntity.Expertise"/> +
/// <see cref="UnitMembershipHumanEntity.Notifications"/> in place rather
/// than returning 409 — the same auto-seed pattern adopted by the
/// connector-identity surface (#2408 / #2420).
/// </para>
/// </remarks>
public static class UnitTeamMembershipEndpoints
{
    /// <summary>
    /// Registers the team-role membership routes under the existing
    /// <c>/api/v1/tenant/units</c> group. The group-level
    /// <c>RequireAuthorization()</c> in <c>Program.cs</c> applies the
    /// tenant-user authentication gate; per-endpoint Owner / Viewer checks
    /// run inside each handler via <see cref="UnitPermissionCheck"/>.
    /// </summary>
    public static RouteGroupBuilder MapUnitTeamMembershipEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/units")
            .WithTags("Units");

        group.MapGet("/{id}/members/humans", ListAsync)
            .WithName("ListUnitHumanMembers")
            .WithSummary("List every team-role membership row attached to this unit.")
            .WithDescription("Returns the rows from `unit_memberships_humans` for the unit in stable (created_at, id) order. Mirror of `sv.list_members`'s human entries. Viewer-gated.")
            .Produces<IReadOnlyList<UnitHumanMemberResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/members/humans", AddAsync)
            .WithName("AddUnitHumanMember")
            .WithSummary("Add a human as a unit team-role member.")
            .WithDescription("Idempotent on the natural key (unit, human, role) — re-posting the same tuple updates expertise + notifications in place rather than returning 409. Owner-gated.")
            .Produces<UnitHumanMemberResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{id}/members/humans/{humanId:guid}/{role}", UpdateAsync)
            .WithName("UpdateUnitHumanMember")
            .WithSummary("Update expertise / notifications on an existing team-role membership row.")
            .WithDescription("Replaces the whole expertise + notifications tag sets — omitted properties are treated as empty lists. Owner-gated; 404 when no row matches.")
            .Produces<UnitHumanMemberResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}/members/humans/{humanId:guid}/{role}", RemoveAsync)
            .WithName("RemoveUnitHumanMember")
            .WithSummary("Remove a team-role membership row.")
            .WithDescription("Idempotent — returns 204 whether or not a matching row existed. Owner-gated.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> ListAsync(
        string id,
        HttpContext httpContext,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IPermissionService permissionService,
        [FromServices] IUnitHumanMembershipStore membershipStore,
        CancellationToken cancellationToken)
    {
        var auth = await UnitPermissionCheck.AuthorizeAsync(
            id,
            PermissionLevel.Viewer,
            directoryService,
            permissionService,
            httpContext,
            cancellationToken);
        if (!auth.Authorized)
        {
            return auth.ToErrorResult(id);
        }

        var rows = await membershipStore.ListByUnitAsync(auth.Entry!.ActorId, cancellationToken);
        return Results.Ok(rows.Select(ToResponse).ToList());
    }

    private static async Task<IResult> AddAsync(
        string id,
        AddUnitHumanMemberRequest request,
        HttpContext httpContext,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IPermissionService permissionService,
        [FromServices] IUnitHumanMembershipStore membershipStore,
        [FromServices] SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem(
                detail: "Request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var role = (request.Role ?? string.Empty).Trim();
        if (role.Length == 0)
        {
            return Results.Problem(
                detail: "'role' is required and must be non-empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.HumanId == Guid.Empty)
        {
            return Results.Problem(
                detail: "'humanId' is required and must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await UnitPermissionCheck.AuthorizeAsync(
            id,
            PermissionLevel.Owner,
            directoryService,
            permissionService,
            httpContext,
            cancellationToken);
        if (!auth.Authorized)
        {
            return auth.ToErrorResult(id);
        }

        // The human must exist in the current tenant so the membership row
        // never points at a foreign / non-existent human. The tenant query
        // filter on the DbContext scopes the lookup automatically.
        var humanExists = await db.Humans
            .AsNoTracking()
            .AnyAsync(h => h.Id == request.HumanId, cancellationToken);
        if (!humanExists)
        {
            return Results.Problem(
                detail: $"Human '{request.HumanId:N}' was not found in the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var expertise = NormaliseTags(request.Expertise);
        var notifications = NormaliseTags(request.Notifications);

        var row = await membershipStore.UpsertAsync(
            auth.Entry!.ActorId,
            request.HumanId,
            role,
            expertise,
            notifications,
            cancellationToken);

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> UpdateAsync(
        string id,
        Guid humanId,
        string role,
        UpdateUnitHumanMemberRequest request,
        HttpContext httpContext,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IPermissionService permissionService,
        [FromServices] IUnitHumanMembershipStore membershipStore,
        CancellationToken cancellationToken)
    {
        var body = request ?? new UpdateUnitHumanMemberRequest();

        var trimmedRole = (role ?? string.Empty).Trim();
        if (trimmedRole.Length == 0)
        {
            return Results.Problem(
                detail: "'role' segment in the URL must be non-empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (humanId == Guid.Empty)
        {
            return Results.Problem(
                detail: "'humanId' segment in the URL must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await UnitPermissionCheck.AuthorizeAsync(
            id,
            PermissionLevel.Owner,
            directoryService,
            permissionService,
            httpContext,
            cancellationToken);
        if (!auth.Authorized)
        {
            return auth.ToErrorResult(id);
        }

        var existing = await membershipStore.GetAsync(
            auth.Entry!.ActorId, humanId, trimmedRole, cancellationToken);
        if (existing is null)
        {
            return Results.Problem(
                detail: $"No membership row exists for human '{humanId:N}' with role '{trimmedRole}' on unit '{id}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var expertise = NormaliseTags(body.Expertise);
        var notifications = NormaliseTags(body.Notifications);

        var row = await membershipStore.UpsertAsync(
            auth.Entry!.ActorId,
            humanId,
            trimmedRole,
            expertise,
            notifications,
            cancellationToken);

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> RemoveAsync(
        string id,
        Guid humanId,
        string role,
        HttpContext httpContext,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IPermissionService permissionService,
        [FromServices] IUnitHumanMembershipStore membershipStore,
        CancellationToken cancellationToken)
    {
        var trimmedRole = (role ?? string.Empty).Trim();
        if (trimmedRole.Length == 0 || humanId == Guid.Empty)
        {
            // The route constraint already rejects an unparseable humanId
            // with 404 / 400 before the handler runs, but a whitespace-only
            // role segment still reaches here and should surface as 400 so
            // the CLI can give a precise error instead of a silent 204.
            return Results.Problem(
                detail: "'role' segment in the URL must be non-empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await UnitPermissionCheck.AuthorizeAsync(
            id,
            PermissionLevel.Owner,
            directoryService,
            permissionService,
            httpContext,
            cancellationToken);
        if (!auth.Authorized)
        {
            return auth.ToErrorResult(id);
        }

        await membershipStore.RemoveAsync(auth.Entry!.ActorId, humanId, trimmedRole, cancellationToken);
        return Results.NoContent();
    }

    private static List<string> NormaliseTags(IReadOnlyList<string>? raw) =>
        (raw ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList();

    private static UnitHumanMemberResponse ToResponse(UnitHumanMembership row) =>
        new(row.MembershipId, row.HumanId, row.Role, row.Expertise, row.Notifications);
}
