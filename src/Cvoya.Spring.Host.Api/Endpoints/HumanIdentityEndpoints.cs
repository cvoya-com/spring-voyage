// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Security.Claims;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// REST surface for the human ↔ connector-native identity mapping table
/// (#2408). The table backs <see cref="IHumanConnectorIdentityResolver"/>;
/// these endpoints let an operator manage the rows via the CLI
/// (<c>spring human identity set/list/remove</c>) before the portal
/// surface lands in v0.2.
/// </summary>
/// <remarks>
/// <para>
/// All routes are gated to <see cref="RolePolicies.TenantUser"/> via the
/// group binding in <c>Program.cs</c>. The implicit policy for "who can
/// edit whose identities" today is "the caller can manage any human in
/// the current tenant"; that matches the OSS Operator default and
/// pre-dates the per-Human admin role we'll introduce in v0.2. The cloud
/// overlay tightens this once tenant-multi-user is real.
/// </para>
/// <para>
/// Endpoint shape follows the per-Human sub-route pattern requested in
/// #2408: <c>/api/v1/tenant/humans/{id}/identities</c>. A separate top-
/// level humans endpoint group is created here because the existing
/// <c>UnitEndpoints</c> per-human routes are unit-scoped permission
/// grants — orthogonal to the per-human external-identity surface.
/// </para>
/// </remarks>
public static class HumanIdentityEndpoints
{
    /// <summary>
    /// Registers the human ↔ connector identity routes under
    /// <c>/api/v1/tenant/humans</c>.
    /// </summary>
    public static RouteGroupBuilder MapHumanIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/humans")
            .WithTags("Humans");

        group.MapPost("/{humanId:guid}/identities", UpsertIdentityAsync)
            .WithName("UpsertHumanConnectorIdentity")
            .WithSummary("Create or update a human ↔ connector identity mapping")
            .Produces<HumanConnectorIdentityResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{humanId:guid}/identities", ListIdentitiesAsync)
            .WithName("ListHumanConnectorIdentities")
            .WithSummary("List every connector identity row mapped to this human")
            .Produces<IReadOnlyList<HumanConnectorIdentityResponse>>(StatusCodes.Status200OK);

        group.MapDelete("/{humanId:guid}/identities", RemoveIdentityAsync)
            .WithName("RemoveHumanConnectorIdentity")
            .WithSummary("Remove a connector identity mapping. Idempotent — returns 204 even when nothing matched.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return group;
    }

    private static async Task<IResult> UpsertIdentityAsync(
        Guid humanId,
        [FromBody] HumanConnectorIdentityRequest request,
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
        var connectorUserId = (request.ConnectorUserId ?? string.Empty).Trim();
        var displayHandle = string.IsNullOrWhiteSpace(request.DisplayHandle)
            ? null
            : request.DisplayHandle.Trim();

        if (string.IsNullOrWhiteSpace(connectorId) || string.IsNullOrWhiteSpace(connectorUserId))
        {
            return Results.Problem(
                detail: "Both 'connectorId' and 'connectorUserId' are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (humanId == Guid.Empty)
        {
            return Results.Problem(
                detail: "Human id must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Ensure the human exists in the current tenant so the row never
        // points at a foreign / non-existent human. The tenant query
        // filter on the DbContext keeps this scoped automatically.
        var humanExists = await db.Humans
            .AsNoTracking()
            .AnyAsync(h => h.Id == humanId, cancellationToken);
        if (!humanExists)
        {
            return Results.Problem(
                detail: $"Human '{humanId:N}' was not found in the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Existing row for the same (connector, user_id) in the tenant —
        // either same human (update) or different human (409 collision).
        var existing = await db.HumanConnectorIdentities
            .FirstOrDefaultAsync(
                e => e.ConnectorId == connectorId && e.ConnectorUserId == connectorUserId,
                cancellationToken);

        if (existing is not null && existing.HumanId != humanId)
        {
            return Results.Problem(
                title: "Connector identity already claimed",
                detail: $"Connector identity '{connectorId}:{connectorUserId}' is already mapped to a different human.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (existing is not null)
        {
            // Same human → in-place update of the display handle.
            existing.DisplayHandle = displayHandle;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(ToResponse(existing));
        }

        var row = new HumanConnectorIdentityEntity
        {
            Id = Guid.NewGuid(),
            HumanId = humanId,
            ConnectorId = connectorId,
            ConnectorUserId = connectorUserId,
            DisplayHandle = displayHandle,
        };

        try
        {
            db.HumanConnectorIdentities.Add(row);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Race: a concurrent writer landed the same (tenant, connector,
            // user_id) tuple between the SELECT above and our INSERT. The
            // unique index ux_human_connector_identities_tenant_connector_user
            // is what catches it.
            db.Entry(row).State = EntityState.Detached;
            return Results.Problem(
                title: "Connector identity already claimed",
                detail: $"Connector identity '{connectorId}:{connectorUserId}' was registered concurrently by another caller.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> ListIdentitiesAsync(
        Guid humanId,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var rows = await db.HumanConnectorIdentities
            .AsNoTracking()
            .Where(e => e.HumanId == humanId)
            .OrderBy(e => e.ConnectorId).ThenBy(e => e.ConnectorUserId)
            .ToListAsync(cancellationToken);

        return Results.Ok(rows.ConvertAll(ToResponse));
    }

    private static async Task<IResult> RemoveIdentityAsync(
        Guid humanId,
        [FromQuery] string? connectorId,
        [FromQuery] string? connectorUserId,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var c = (connectorId ?? string.Empty).Trim();
        var u = (connectorUserId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(c) || string.IsNullOrWhiteSpace(u))
        {
            return Results.Problem(
                detail: "Both 'connectorId' and 'connectorUserId' query parameters are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var row = await db.HumanConnectorIdentities
            .FirstOrDefaultAsync(
                e => e.HumanId == humanId && e.ConnectorId == c && e.ConnectorUserId == u,
                cancellationToken);
        if (row is null)
        {
            // Idempotent contract: removing a row that doesn't exist still
            // returns 204 so the CLI doesn't have to branch on prior state.
            return Results.NoContent();
        }

        db.HumanConnectorIdentities.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static HumanConnectorIdentityResponse ToResponse(HumanConnectorIdentityEntity row) =>
        new(row.HumanId, row.ConnectorId, row.ConnectorUserId, row.DisplayHandle, row.CreatedAt, row.UpdatedAt);
}
