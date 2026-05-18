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
/// ADR-0044 § 3, reshaped by ADR-0045 §7). Mounted under
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
/// ADR-0045 §7 collapses the natural key to <c>(unit, human)</c>; the
/// PATCH / DELETE routes are keyed by <c>{humanId}</c> alone. POST is
/// idempotent on the same natural key — re-posting the same tuple
/// updates roles / expertise / notifications in place rather than
/// returning 409.
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
            .WithDescription("Idempotent on the natural key (unit, human) — re-posting the same tuple updates roles + expertise + notifications in place rather than returning 409. Owner-gated.")
            .Produces<UnitHumanMemberResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{id}/members/humans/{humanId:guid}", UpdateAsync)
            .WithName("UpdateUnitHumanMember")
            .WithSummary("Update roles / expertise / notifications on an existing team-role membership row.")
            .WithDescription("Multi-valued fields use replace semantics — omitted properties are treated as empty lists. Owner-gated; 404 when no row matches.")
            .Produces<UnitHumanMemberResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}/members/humans/{humanId:guid}", RemoveAsync)
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

        var roles = NormaliseTags(request.Roles);
        var expertise = NormaliseTags(request.Expertise);
        var notifications = NormaliseTags(request.Notifications);

        var row = await membershipStore.UpsertAsync(
            auth.Entry!.ActorId,
            request.HumanId,
            roles,
            expertise,
            notifications,
            cancellationToken);

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> UpdateAsync(
        string id,
        Guid humanId,
        UpdateUnitHumanMemberRequest request,
        HttpContext httpContext,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IPermissionService permissionService,
        [FromServices] IUnitHumanMembershipStore membershipStore,
        CancellationToken cancellationToken)
    {
        var body = request ?? new UpdateUnitHumanMemberRequest();

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
            auth.Entry!.ActorId, humanId, cancellationToken);
        if (existing is null)
        {
            return Results.Problem(
                detail: $"No membership row exists for human '{humanId:N}' on unit '{id}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // PATCH semantics per ADR-0045 §5: each multi-valued field is a
        // full-replacement set. When the caller omits a list (sends null),
        // the existing list is preserved — only an explicit empty array
        // clears the field. This matches the wire-level affordance the
        // portal Member tab needs to "edit expertise without disturbing
        // roles".
        var roles = body.Roles is null ? existing.Roles : NormaliseTags(body.Roles);
        var expertise = body.Expertise is null ? existing.Expertise : NormaliseTags(body.Expertise);
        var notifications = body.Notifications is null
            ? existing.Notifications
            : NormaliseTags(body.Notifications);

        var row = await membershipStore.UpsertAsync(
            auth.Entry!.ActorId,
            humanId,
            roles,
            expertise,
            notifications,
            cancellationToken);

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> RemoveAsync(
        string id,
        Guid humanId,
        HttpContext httpContext,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IPermissionService permissionService,
        [FromServices] IUnitHumanMembershipStore membershipStore,
        CancellationToken cancellationToken)
    {
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

        await membershipStore.RemoveAsync(auth.Entry!.ActorId, humanId, cancellationToken);
        return Results.NoContent();
    }

    private static List<string> NormaliseTags(IReadOnlyList<string>? raw) =>
        (raw ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList();

    private static UnitHumanMemberResponse ToResponse(UnitHumanMembership row) =>
        new(row.MembershipId, row.HumanId, row.Roles, row.Expertise, row.Notifications);
}
