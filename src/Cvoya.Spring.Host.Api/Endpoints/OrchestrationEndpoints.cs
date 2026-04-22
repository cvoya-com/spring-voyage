// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Unit-orchestration endpoints (#606). Exposes
/// <c>GET / PUT / DELETE /api/v1/units/{id}/orchestration</c> — the direct
/// read/write surface for the manifest-persisted
/// <c>orchestration.strategy</c> slot that ADR-0010 deferred.
/// </summary>
/// <remarks>
/// <para>
/// A unit that has never had a strategy persisted returns the empty shape
/// (<c>{ "strategy": null }</c>) — callers never need to branch on 404 vs
/// unset, matching the <c>/policy</c> and <c>/boundary</c> conventions.
/// Writes go through <see cref="IUnitOrchestrationStore"/> which fires the
/// <see cref="IOrchestrationStrategyCacheInvalidator"/> configured at DI
/// time, so the per-message resolver (ADR-0010) sees the new key on the
/// next dispatch instead of waiting for the cache TTL to expire.
/// </para>
/// <para>
/// Strategy-key validation is intentionally lenient: the endpoint accepts
/// any non-empty string so a host that registers additional
/// <see cref="IOrchestrationStrategy"/> implementations (private cloud
/// repo; custom on-prem overlays) can expose their keys without a
/// whitelist. The resolver degrades to the policy-inferred / unkeyed
/// default when it cannot find a DI registration under the declared key,
/// so a misconfigured write never takes the unit offline. Custom-key
/// validation is tracked separately under #605.
/// </para>
/// </remarks>
public static class OrchestrationEndpoints
{
    /// <summary>
    /// Registers the unit-orchestration endpoints on the supplied route
    /// builder.
    /// </summary>
    public static IEndpointRouteBuilder MapOrchestrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/units/{id}/orchestration")
            .WithTags("UnitOrchestration")
            .RequireAuthorization();

        group.MapGet("/", GetOrchestrationAsync)
            .WithName("GetUnitOrchestration")
            .WithSummary("Get the unit's persisted orchestration strategy")
            .Produces<UnitOrchestrationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/", SetOrchestrationAsync)
            .WithName("SetUnitOrchestration")
            .WithSummary("Upsert the unit's orchestration strategy")
            .Produces<UnitOrchestrationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/", ClearOrchestrationAsync)
            .WithName("ClearUnitOrchestration")
            .WithSummary("Clear the unit's orchestration strategy (fall back to inferred / default)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetOrchestrationAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitOrchestrationStore store,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("unit", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var strategy = await store.GetStrategyKeyAsync(id, cancellationToken);
        return Results.Ok(new UnitOrchestrationResponse(strategy));
    }

    private static async Task<IResult> SetOrchestrationAsync(
        string id,
        UnitOrchestrationResponse request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitOrchestrationStore store,
        CancellationToken cancellationToken)
    {
        // #606: reject an empty / whitespace strategy on PUT so the caller
        // sees a clear 400. Clearing the slot is a separate verb (DELETE)
        // rather than a silent write of an empty key — matches the
        // /boundary endpoint's split between PUT-with-empty-shape (valid
        // "no rules" upsert) and DELETE (explicit clear).
        if (string.IsNullOrWhiteSpace(request.Strategy))
        {
            return Results.Problem(
                detail: "Orchestration strategy must be a non-empty string. Use DELETE to clear the slot.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var entry = await directoryService.ResolveAsync(new Address("unit", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        await store.SetStrategyKeyAsync(id, request.Strategy, cancellationToken);

        // Re-read so the client sees the canonical post-write shape (in
        // particular the server-side trim applied to the key).
        var stored = await store.GetStrategyKeyAsync(id, cancellationToken);
        return Results.Ok(new UnitOrchestrationResponse(stored));
    }

    private static async Task<IResult> ClearOrchestrationAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitOrchestrationStore store,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("unit", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        await store.SetStrategyKeyAsync(id, strategyKey: null, cancellationToken);
        return Results.NoContent();
    }
}