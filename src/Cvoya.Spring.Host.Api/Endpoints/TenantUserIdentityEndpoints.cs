// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// REST surface for the <c>TenantUser</c> envelope and its connector
/// display-identity rows (ADR-0047 §2 + §14). Replaces the prior
/// <c>HumanIdentityEndpoints.cs</c>'s <c>/identities</c> subset; the
/// per-<c>Human</c> envelope routes stay where they are because they are
/// unit-membership concerns owned by ADR-0046.
/// </summary>
/// <remarks>
/// <para>
/// Routes:
/// <list type="bullet">
///   <item><description><c>GET    /api/v1/tenant/users/{tenantUserId}</c> — read envelope.</description></item>
///   <item><description><c>PATCH  /api/v1/tenant/users/{tenantUserId}</c> — update displayName / description.</description></item>
///   <item><description><c>POST   /api/v1/tenant/users/{tenantUserId}/identities</c> — upsert identity row.</description></item>
///   <item><description><c>GET    /api/v1/tenant/users/{tenantUserId}/identities</c> — list identities.</description></item>
///   <item><description><c>DELETE /api/v1/tenant/users/{tenantUserId}/identities?connectorId=&amp;username=</c> — remove identity row.</description></item>
/// </list>
/// </para>
/// <para>
/// All routes are gated to <see cref="RolePolicies.TenantUser"/> via the
/// group binding in <c>Program.cs</c>. The implicit policy for "who can
/// edit whose identities" today is "the caller can manage any tenant
/// user in the current tenant" — that matches the OSS Operator default
/// and pre-dates the per-Human admin role. The cloud overlay tightens
/// this once tenant-multi-user is real.
/// </para>
/// </remarks>
public static class TenantUserIdentityEndpoints
{
    /// <summary>
    /// Registers the <c>TenantUser</c> envelope and identity routes under
    /// <c>/api/v1/tenant/users</c>.
    /// </summary>
    public static RouteGroupBuilder MapTenantUserIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/users")
            .WithTags("TenantUsers");

        group.MapGet("/{tenantUserId:guid}", GetTenantUserAsync)
            .WithName("GetTenantUser")
            .WithSummary("Read a single tenant user's read-side envelope.")
            .WithDescription("Returns the canonical fields needed by the portal's user-identity page and the CLI's read verbs. Identity lists live on the dedicated /identities sub-resource and are NOT embedded here.")
            .Produces<TenantUserResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{tenantUserId:guid}", UpdateTenantUserAsync)
            .WithName("UpdateTenantUser")
            .WithSummary("Update a tenant user's editable identity fields (display name, description).")
            .WithDescription("Omitted fields leave the existing value untouched (PATCH semantics). DisplayName is validated via DisplayNameProblems.ValidateOrProblem; description has no length limit. Returns the post-write TenantUserResponse.")
            .Produces<TenantUserResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{tenantUserId:guid}/identities", UpsertIdentityAsync)
            .WithName("UpsertTenantUserConnectorIdentity")
            .WithSummary("Create or update a tenant-user ↔ connector display-identity mapping (ADR-0047 §2).")
            .Produces<TenantUserConnectorIdentityResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{tenantUserId:guid}/identities", ListIdentitiesAsync)
            .WithName("ListTenantUserConnectorIdentities")
            .WithSummary("List every connector identity row mapped to this tenant user.")
            .Produces<IReadOnlyList<TenantUserConnectorIdentityResponse>>(StatusCodes.Status200OK);

        group.MapDelete("/{tenantUserId:guid}/identities", RemoveIdentityAsync)
            .WithName("RemoveTenantUserConnectorIdentity")
            .WithSummary("Remove a connector identity mapping by (tenantUser, connector, username). Idempotent — returns 204 even when nothing matched.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return group;
    }

    private static async Task<IResult> GetTenantUserAsync(
        Guid tenantUserId,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (tenantUserId == Guid.Empty)
        {
            return Results.Problem(
                detail: "Tenant user id must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var row = await db.TenantUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == tenantUserId, cancellationToken);
        if (row is null)
        {
            return Results.Problem(
                detail: $"Tenant user '{tenantUserId:N}' was not found in the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> UpdateTenantUserAsync(
        Guid tenantUserId,
        [FromBody] UpdateTenantUserRequest? request,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (tenantUserId == Guid.Empty)
        {
            return Results.Problem(
                detail: "Tenant user id must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var body = request ?? new UpdateTenantUserRequest();

        // Validate only when the caller actually supplied a new display
        // name — null means "leave unchanged" on the PATCH surface.
        if (body.DisplayName is not null)
        {
            var displayNameProblem = DisplayNameProblems.ValidateOrProblem(body.DisplayName);
            if (displayNameProblem is not null)
            {
                return displayNameProblem;
            }
        }

        var row = await db.TenantUsers
            .FirstOrDefaultAsync(u => u.Id == tenantUserId, cancellationToken);
        if (row is null)
        {
            return Results.Problem(
                detail: $"Tenant user '{tenantUserId:N}' was not found in the current tenant.",
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

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> UpsertIdentityAsync(
        Guid tenantUserId,
        [FromBody] TenantUserConnectorIdentityRequest request,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem(
                detail: "Request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var connectorId = (request.ConnectorId ?? string.Empty).Trim();
        var username = (request.Username ?? string.Empty).Trim();
        var displayHandle = string.IsNullOrWhiteSpace(request.DisplayHandle)
            ? null
            : request.DisplayHandle.Trim();

        if (string.IsNullOrWhiteSpace(connectorId) || string.IsNullOrWhiteSpace(username))
        {
            return Results.Problem(
                detail: "Both 'connectorId' and 'username' are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (tenantUserId == Guid.Empty)
        {
            return Results.Problem(
                detail: "Tenant user id must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Tenant query filter on the DbContext scopes this lookup
        // automatically — a cross-tenant id surfaces as a clean 404.
        var tenantUserExists = await db.TenantUsers
            .AsNoTracking()
            .AnyAsync(u => u.Id == tenantUserId, cancellationToken);
        if (!tenantUserExists)
        {
            return Results.Problem(
                detail: $"Tenant user '{tenantUserId:N}' was not found in the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Existing row for the same (connector, username) — either the
        // same tenant user (in-place display-handle update) or a different
        // one (409 collision; the reverse-lookup unique constraint per
        // ADR-0047 §2 enforces "one connector login per tenant user").
        var existingByLogin = await db.TenantUserConnectorIdentities
            .FirstOrDefaultAsync(
                e => e.ConnectorId == connectorId && e.Username == username,
                cancellationToken);

        if (existingByLogin is not null && existingByLogin.TenantUserId != tenantUserId)
        {
            return Results.Problem(
                title: "Connector identity already claimed",
                detail: $"Connector identity '{connectorId}:{username}' is already mapped to a different tenant user.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // Also check the natural-key row "this tenant user already has an
        // identity on this connector" — that path collapses to an
        // in-place update of (username, displayHandle) so re-running
        // `spring user identity set --connector github --username new`
        // overwrites instead of conflicting on the reverse-lookup index.
        var existingByPair = existingByLogin ?? await db.TenantUserConnectorIdentities
            .FirstOrDefaultAsync(
                e => e.TenantUserId == tenantUserId && e.ConnectorId == connectorId,
                cancellationToken);

        if (existingByPair is not null)
        {
            existingByPair.Username = username;
            existingByPair.DisplayHandle = displayHandle;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(ToResponse(existingByPair));
        }

        var row = new TenantUserConnectorIdentityEntity
        {
            Id = Guid.NewGuid(),
            TenantUserId = tenantUserId,
            ConnectorId = connectorId,
            Username = username,
            DisplayHandle = displayHandle,
        };

        try
        {
            db.TenantUserConnectorIdentities.Add(row);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Race: a concurrent writer landed the same tuple between the
            // SELECT and INSERT. Either of the two unique indices may have
            // caught it.
            db.Entry(row).State = EntityState.Detached;
            return Results.Problem(
                title: "Connector identity already claimed",
                detail: $"Connector identity '{connectorId}:{username}' was registered concurrently by another caller.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> ListIdentitiesAsync(
        Guid tenantUserId,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var rows = await db.TenantUserConnectorIdentities
            .AsNoTracking()
            .Where(e => e.TenantUserId == tenantUserId)
            .OrderBy(e => e.ConnectorId).ThenBy(e => e.Username)
            .ToListAsync(cancellationToken);

        return Results.Ok(rows.ConvertAll(ToResponse));
    }

    private static async Task<IResult> RemoveIdentityAsync(
        Guid tenantUserId,
        [FromQuery] string? connectorId,
        [FromQuery] string? username,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var c = (connectorId ?? string.Empty).Trim();
        var u = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(c) || string.IsNullOrWhiteSpace(u))
        {
            return Results.Problem(
                detail: "Both 'connectorId' and 'username' query parameters are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var row = await db.TenantUserConnectorIdentities
            .FirstOrDefaultAsync(
                e => e.TenantUserId == tenantUserId && e.ConnectorId == c && e.Username == u,
                cancellationToken);
        if (row is null)
        {
            // Idempotent contract: removing a row that doesn't exist still
            // returns 204 so the CLI doesn't have to branch on prior state.
            return Results.NoContent();
        }

        db.TenantUserConnectorIdentities.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static TenantUserResponse ToResponse(TenantUserEntity row) =>
        new(row.Id, row.AuthSubject, row.DisplayName, row.Description, row.CreatedAt, row.UpdatedAt);

    private static TenantUserConnectorIdentityResponse ToResponse(TenantUserConnectorIdentityEntity row) =>
        new(row.TenantUserId, row.ConnectorId, row.Username, row.DisplayHandle, row.CreatedAt, row.UpdatedAt);
}
