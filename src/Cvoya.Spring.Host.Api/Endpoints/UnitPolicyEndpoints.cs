// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Http;
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
///
/// <para>
/// <b>Authorisation ordering.</b> Permission checks run <em>after</em> the
/// existence probe inside each handler (via
/// <see cref="UnitPermissionCheck.AuthorizeAsync"/>) rather than through
/// <c>RequireAuthorization(PermissionPolicies.Unit*)</c> on the route.
/// The declarative gate evaluated authorisation before the handler ran
/// and failed closed on an unknown unit — surfacing 403 instead of 404
/// and leaking existence (#1029). Authentication still runs ahead of the
/// handler via the group-level <c>RequireAuthorization()</c> call in
/// <c>Program.cs</c>.
/// </para>
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
        var group = app.MapGroup("/api/v1/tenant/units/{id}/policy")
            .WithTags("UnitPolicy");

        group.MapGet("/", GetPolicyAsync)
            .WithName("GetUnitPolicy")
            .WithSummary("Get the unit's governance policy")
            .Produces<UnitPolicyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/", SetPolicyAsync)
            .WithName("SetUnitPolicy")
            .WithSummary("Upsert the unit's governance policy")
            .Produces<UnitPolicyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> GetPolicyAsync(
        string id,
        HttpContext httpContext,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IPermissionService permissionService,
        [FromServices] IUnitPolicyRepository repository,
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

        // Use the stable ActorId (UUID) as the policy key, not the slug from
        // the URL. Slugs are reused when a unit is deleted and recreated with
        // the same name; using the UUID ensures the new unit sees no policy
        // inherited from the old one (#1488).
        var actorId = auth.Entry!.ActorId;
        var policy = await repository.GetAsync(actorId, cancellationToken);
        return Results.Ok(UnitPolicyResponse.From(policy));
    }

    private static async Task<IResult> SetPolicyAsync(
        string id,
        UnitPolicyResponse request,
        HttpContext httpContext,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IPermissionService permissionService,
        [FromServices] IUnitPolicyRepository repository,
        CancellationToken cancellationToken)
    {
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

        // Use the stable ActorId (UUID) as the policy key, not the slug from
        // the URL. Slugs are reused when a unit is deleted and recreated with
        // the same name; using the UUID ensures the new unit's policy does not
        // collide with or overwrite the old one's (#1488).
        var actorId = auth.Entry!.ActorId;
        var policy = request.ToCore();
        await repository.SetAsync(actorId, policy, cancellationToken);

        // Re-read so the client sees the canonical post-write shape —
        // in particular, empty policies come back as UnitPolicy.Empty
        // regardless of what was sent.
        var stored = await repository.GetAsync(actorId, cancellationToken);
        return Results.Ok(UnitPolicyResponse.From(stored));
    }
}