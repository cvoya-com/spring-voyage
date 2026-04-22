// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Unit-boundary endpoints (#413). Exposes
/// <c>GET / PUT /api/v1/units/{id}/boundary</c>. A unit that has never had a
/// boundary persisted returns the empty shape — callers never need to branch
/// on 404 vs empty-boundary, mirroring the <c>/policy</c> endpoint's
/// behaviour.
/// </summary>
/// <remarks>
/// Writes invalidate the aggregator cache for the unit and every ancestor
/// so the next aggregated-expertise read sees the new rules on the outside
/// view.
/// </remarks>
public static class BoundaryEndpoints
{
    /// <summary>
    /// Registers the unit-boundary endpoints on the supplied route builder.
    /// </summary>
    public static IEndpointRouteBuilder MapBoundaryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/units/{id}/boundary")
            .WithTags("UnitBoundary")
            .RequireAuthorization();

        group.MapGet("/", GetBoundaryAsync)
            .WithName("GetUnitBoundary")
            .WithSummary("Get the unit's boundary configuration")
            .Produces<UnitBoundaryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/", SetBoundaryAsync)
            .WithName("SetUnitBoundary")
            .WithSummary("Upsert the unit's boundary configuration")
            .Produces<UnitBoundaryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/", ClearBoundaryAsync)
            .WithName("ClearUnitBoundary")
            .WithSummary("Clear every boundary rule on the unit")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetBoundaryAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitBoundaryStore boundaryStore,
        CancellationToken cancellationToken)
    {
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var boundary = await boundaryStore.GetAsync(address, cancellationToken);
        return Results.Ok(UnitBoundaryResponse.From(boundary));
    }

    private static async Task<IResult> SetBoundaryAsync(
        string id,
        UnitBoundaryResponse request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitBoundaryStore boundaryStore,
        [FromServices] IExpertiseAggregator aggregator,
        CancellationToken cancellationToken)
    {
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var boundary = request.ToCore();
        await boundaryStore.SetAsync(address, boundary, cancellationToken);

        // The boundary decorator only reads the config on each GetAsync(...,
        // BoundaryViewContext, ...) call — it does not cache rules. But the
        // inner aggregator's cached snapshot is fine to keep; invalidation is
        // still called so any stale aggregate on a mid-tree change surfaces
        // fresh.
        await aggregator.InvalidateAsync(address, cancellationToken);

        var stored = await boundaryStore.GetAsync(address, cancellationToken);
        return Results.Ok(UnitBoundaryResponse.From(stored));
    }

    private static async Task<IResult> ClearBoundaryAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitBoundaryStore boundaryStore,
        [FromServices] IExpertiseAggregator aggregator,
        CancellationToken cancellationToken)
    {
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        await boundaryStore.SetAsync(address, UnitBoundary.Empty, cancellationToken);
        await aggregator.InvalidateAsync(address, cancellationToken);
        return Results.NoContent();
    }
}