// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Per-<c>Human</c> read-side envelope routes (#2266 / #2267, ADR-0046 §7).
/// Lives at <c>/api/v1/tenant/humans/{humanId}</c> and stays there per
/// ADR-0047 §14: connector identity moves to the <c>TenantUser</c> surface,
/// but the <c>Human</c> envelope itself remains a unit-membership concern
/// owned by ADR-0046.
/// </summary>
/// <remarks>
/// <para>
/// Previously bundled with the connector-identity sub-routes inside
/// <c>HumanIdentityEndpoints.cs</c>; ADR-0047 splits the two concerns so
/// the envelope routes have a focused file and the connector-identity
/// surface lives on its own endpoint group
/// (<see cref="TenantUserIdentityEndpoints"/>).
/// </para>
/// <para>
/// All routes are gated to <see cref="RolePolicies.TenantUser"/> via the
/// group binding in <c>Program.cs</c>.
/// </para>
/// </remarks>
public static class HumanEnvelopeEndpoints
{
    /// <summary>
    /// Registers the per-<c>Human</c> envelope routes under
    /// <c>/api/v1/tenant/humans</c>.
    /// </summary>
    public static RouteGroupBuilder MapHumanEnvelopeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/humans")
            .WithTags("Humans");

        // ADR-0062 § 6: mint a new Human (Hat) outside of the package-
        // install path. Backs `spring unit members humans add
        // --display-name <...> --as <tenant-user-ref>` and the portal's
        // create-Hat affordance; the existing `POST /units/{id}/members/
        // humans` endpoint then attaches the freshly-minted row to a
        // unit. Keeps the create + attach concerns on two endpoints
        // because the same Hat is reusable across units (#2808 / ADR-0062).
        group.MapPost("/", CreateHumanAsync)
            .WithName("CreateHuman")
            .WithSummary("Mint a new Human (Hat) row outside of the package-install path.")
            .WithDescription("Allocates a fresh Guid, sets the explicit `tenantUserId` (or resolves the deployment default via ITenantUserDefaultResolver) and persists the row. DisplayName is validated via DisplayNameProblems.ValidateOrProblem. Returns the post-write HumanResponse so callers can render the row without a follow-up GET.")
            .Produces<HumanResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // #2266 / #2267: per-human read-side envelope consumed by the
        // Explorer Human page.
        group.MapGet("/{humanId:guid}", GetHumanAsync)
            .WithName("GetHuman")
            .WithSummary("Read a single human's read-side envelope (display name, description, email, platform role, created-at).")
            .WithDescription("Returns the canonical fields needed by the Explorer Human page. Identity / membership lists live on dedicated sub-resources and are NOT embedded here.")
            .Produces<HumanResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // ADR-0046 §7: Human × Config × General PATCH (parallel to the
        // existing agent / unit PATCH surfaces).
        group.MapPatch("/{humanId:guid}", UpdateHumanAsync)
            .WithName("UpdateHuman")
            .WithSummary("Update a human's editable identity fields (display name, description).")
            .WithDescription("Omitted fields leave the existing value untouched (PATCH semantics). DisplayName is validated via DisplayNameProblems.ValidateOrProblem; description has no length limit. Returns the post-write HumanResponse.")
            .Produces<HumanResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // #2649: parallel to DELETE /agents/{id} and DELETE /units/{id} —
        // a human delete cascades to every membership and ACL grant the
        // human holds so the row disappears from every parent unit's
        // members collection in the same write.
        group.MapDelete("/{humanId:guid}", DeleteHumanAsync)
            .WithName("DeleteHuman")
            .WithSummary("Delete a human and cascade-remove every unit-membership row and ACL grant.")
            .WithDescription("Removes the human's row from `humans`, every `unit_membership_humans` row that names this human (so the human disappears from every parent unit's members), and every `unit_human_permissions` row (so stale grants do not survive the delete). Returns 204 on success, 404 when the id is not found in the current tenant, 400 when the id is empty.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> CreateHumanAsync(
        [FromBody] CreateHumanRequest? request,
        [FromServices] SpringDbContext db,
        [FromServices] ITenantUserDefaultResolver tenantUserDefaultResolver,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem(
                detail: "Request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var displayNameProblem = DisplayNameProblems.ValidateOrProblem(request.DisplayName);
        if (displayNameProblem is not null)
        {
            return displayNameProblem;
        }

        // ADR-0062 § 1: every Human-insert path stamps a TenantUser id.
        // The CLI / portal supplies an explicit override when the operator
        // passes `--as <tenant-user-ref>`; otherwise the deployment
        // default resolver returns the OSS operator (or the calling
        // principal under the cloud overlay).
        var tenantUserId = request.TenantUserId ?? Guid.Empty;
        if (tenantUserId == Guid.Empty)
        {
            tenantUserId = await tenantUserDefaultResolver
                .ResolveDefaultAsync(cancellationToken);
        }
        else
        {
            // The tenant-query filter on the DbContext scopes this lookup
            // automatically — a cross-tenant id surfaces as a clean 400
            // ("not found") rather than leaking row existence.
            var tenantUserExists = await db.TenantUsers
                .AsNoTracking()
                .AnyAsync(u => u.Id == tenantUserId, cancellationToken);
            if (!tenantUserExists)
            {
                return Results.Problem(
                    title: "Bad Request",
                    detail: $"TenantUser '{tenantUserId:N}' was not found in the current tenant.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        // The synthetic username must be unique within the tenant per the
        // HumanEntity unique index on (tenant_id, username). Use the freshly-
        // minted Guid to avoid colliding with operator-created humans
        // (whose usernames are JWT subject claims, never of the "cli-mint-"
        // shape).
        var humanId = Guid.NewGuid();
        var displayName = request.DisplayName.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description)
            ? null
            : request.Description.Trim();
        var username = $"cli-mint-{humanId:N}";

        var entity = new HumanEntity
        {
            Id = humanId,
            TenantUserId = tenantUserId,
            Username = username,
            DisplayName = displayName,
            Description = description,
            PermissionLevel = PermissionLevel.Operator,
        };

        try
        {
            db.Humans.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            db.Entry(entity).State = EntityState.Detached;
            return Results.Problem(
                title: "Bad Request",
                detail: $"Failed to insert Human row: {ex.Message}",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var response = new HumanResponse(
            entity.Id,
            entity.Username,
            string.IsNullOrWhiteSpace(entity.DisplayName) ? entity.Username : entity.DisplayName,
            entity.Description,
            entity.Email,
            entity.PermissionLevel.ToString(),
            entity.CreatedAt);

        return Results.Created($"/api/v1/tenant/humans/{entity.Id:D}", response);
    }

    private static async Task<IResult> GetHumanAsync(
        Guid humanId,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (humanId == Guid.Empty)
        {
            return Results.Problem(
                detail: "Human id must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Tenant query filter on the DbContext scopes this to the current
        // tenant automatically — a cross-tenant id surfaces as a clean 404
        // rather than leaking the row's existence.
        var row = await db.Humans
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == humanId, cancellationToken);
        if (row is null)
        {
            return Results.Problem(
                detail: $"Human '{humanId:N}' was not found in the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new HumanResponse(
            row.Id,
            row.Username,
            string.IsNullOrWhiteSpace(row.DisplayName) ? row.Username : row.DisplayName,
            row.Description,
            row.Email,
            row.PermissionLevel.ToString(),
            row.CreatedAt));
    }

    private static async Task<IResult> UpdateHumanAsync(
        Guid humanId,
        [FromBody] UpdateHumanRequest? request,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (humanId == Guid.Empty)
        {
            return Results.Problem(
                detail: "Human id must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var body = request ?? new UpdateHumanRequest();

        if (body.DisplayName is not null)
        {
            var displayNameProblem = DisplayNameProblems.ValidateOrProblem(body.DisplayName);
            if (displayNameProblem is not null)
            {
                return displayNameProblem;
            }
        }

        var row = await db.Humans
            .FirstOrDefaultAsync(h => h.Id == humanId, cancellationToken);
        if (row is null)
        {
            return Results.Problem(
                detail: $"Human '{humanId:N}' was not found in the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (body.DisplayName is not null)
        {
            row.DisplayName = body.DisplayName;
        }
        if (body.Description is not null)
        {
            row.Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        }

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new HumanResponse(
            row.Id,
            row.Username,
            string.IsNullOrWhiteSpace(row.DisplayName) ? row.Username : row.DisplayName,
            row.Description,
            row.Email,
            row.PermissionLevel.ToString(),
            row.CreatedAt));
    }

    private static async Task<IResult> DeleteHumanAsync(
        Guid humanId,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (humanId == Guid.Empty)
        {
            return Results.Problem(
                detail: "Human id must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var row = await db.Humans
            .FirstOrDefaultAsync(h => h.Id == humanId, cancellationToken);
        if (row is null)
        {
            return Results.Problem(
                detail: $"Human '{humanId:N}' was not found in the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // #2649: cascade — remove every membership and grant row that
        // names this human in the same write so the human disappears
        // from every parent unit's members collection (parallel to the
        // agent + unit delete cascades). The two tables capture
        // orthogonal facts (ADR-0044 §1): team-membership rows on the
        // unit, and platform-level ACL grants. Both go.
        var memberships = await db.UnitMembershipsHumans
            .Where(m => m.HumanId == humanId)
            .ToListAsync(cancellationToken);
        if (memberships.Count > 0)
        {
            db.UnitMembershipsHumans.RemoveRange(memberships);
        }

        var permissions = await db.UnitHumanPermissions
            .Where(p => p.HumanId == humanId)
            .ToListAsync(cancellationToken);
        if (permissions.Count > 0)
        {
            db.UnitHumanPermissions.RemoveRange(permissions);
        }

        db.Humans.Remove(row);
        await db.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }
}
