// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Unified unit-policy endpoints introduced by #162. Exposes
/// <c>GET /api/v1/units/{id}/policy</c> and
/// <c>PUT /api/v1/units/{id}/policy</c>. A unit that has never had a policy
/// persisted returns <see cref="UnitPolicy.Empty"/> — callers never need to
/// branch on 404 vs empty-policy. Per-dimension endpoints (e.g.
/// <c>/skill-policy</c>) are deliberately not split out: one endpoint per
/// unit keeps the OpenAPI surface small and makes multi-dimension updates
/// atomic from the client's perspective.
/// </summary>
public static class UnitPolicyEndpoints
{
    /// <summary>
    /// Registers the unit-policy endpoints on the supplied route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group for chaining.</returns>
    public static RouteGroupBuilder MapUnitPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/units/{id}/policy")
            .WithTags("UnitPolicy");

        group.MapGet("/", GetPolicyAsync)
            .WithName("GetUnitPolicy")
            .WithSummary("Get the unit's governance policy")
            .RequireAuthorization(PermissionPolicies.UnitViewer)
            .Produces<UnitPolicyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/", SetPolicyAsync)
            .WithName("SetUnitPolicy")
            .WithSummary("Upsert the unit's governance policy")
            .RequireAuthorization(PermissionPolicies.UnitOwner)
            .Produces<UnitPolicyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> GetPolicyAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitPolicyRepository repository,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("unit", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var policy = await repository.GetAsync(id, cancellationToken);
        return Results.Ok(UnitPolicyResponse.From(policy));
    }

    private static async Task<IResult> SetPolicyAsync(
        string id,
        UnitPolicyResponse request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitPolicyRepository repository,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("unit", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var policy = request.ToCore();
        await repository.SetAsync(id, policy, cancellationToken);

        // Re-read so the client sees the canonical post-write shape —
        // in particular, empty policies come back as UnitPolicy.Empty
        // regardless of what was sent.
        var stored = await repository.GetAsync(id, cancellationToken);
        return Results.Ok(UnitPolicyResponse.From(stored));
    }
}