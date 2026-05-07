// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Unit-execution endpoints (#601 / #603 / #409 B-wide). Exposes
/// <c>GET / PUT / DELETE /api/v1/units/{id}/execution</c> — the
/// direct read/write surface for the manifest-persisted <c>execution:</c>
/// block that holds the unit-level defaults (image / runtime / tool /
/// provider / model) inherited by member agents.
/// </summary>
/// <remarks>
/// <para>
/// A unit that has never had an execution block persisted returns the
/// canonical empty shape (all fields <c>null</c>) — callers never need
/// to branch on 404 vs unset, matching the <c>/orchestration</c> and
/// <c>/policy</c> conventions.
/// </para>
/// <para>
/// PUT semantics are <b>partial update</b>: a non-null field replaces
/// the corresponding slot; a null field leaves the existing persisted
/// value alone. An all-null PUT body is rejected with a 400 — use
/// DELETE to clear. The <c>IUnitExecutionStore</c> behind this surface
/// handles in-place merging so operators can edit one field at a time
/// without resending the whole block.
/// </para>
/// </remarks>
public static class UnitExecutionEndpoints
{
    /// <summary>
    /// Registers the unit-execution endpoints on the supplied route
    /// builder.
    /// </summary>
    public static IEndpointRouteBuilder MapUnitExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/units/{id}/execution")
            .WithTags("UnitExecution")
            .RequireAuthorization(Auth.RolePolicies.TenantUser);

        group.MapGet("/", GetExecutionAsync)
            .WithName("GetUnitExecution")
            .WithSummary("Get the unit's persisted execution defaults")
            .Produces<UnitExecutionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/", SetExecutionAsync)
            .WithName("SetUnitExecution")
            .WithSummary("Upsert one or more fields on the unit's execution defaults (partial update)")
            .Produces<UnitExecutionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/", ClearExecutionAsync)
            .WithName("ClearUnitExecution")
            .WithSummary("Clear the unit's execution defaults (strip the block)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetExecutionAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitExecutionStore store,
        [FromServices] IAgentRuntimeRegistry runtimeRegistry,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("unit", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        // #1748: stores are keyed by the unit's actor Guid. The route
        // already requires `id` to be Guid-shaped (Address.For above
        // throws otherwise), but normalising through entry.ActorId keeps
        // the contract explicit and matches every other unit-keyed surface.
        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        var defaults = await store.GetAsync(actorId, cancellationToken);
        return Results.Ok(ToResponse(defaults, runtimeRegistry));
    }

    private static async Task<IResult> SetExecutionAsync(
        string id,
        UnitExecutionResponse request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitExecutionStore store,
        [FromServices] IAgentRuntimeRegistry runtimeRegistry,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("unit", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        // #1732: ToolKind is read-only on the wire — derived from Agent.
        // The request's ToolKind value (if any) is silently ignored.
        var defaults = new UnitExecutionDefaults(
            Image: request.Image,
            Runtime: request.Runtime,
            Provider: request.Provider,
            Model: request.Model,
            Agent: request.Agent);

        if (defaults.IsEmpty)
        {
            return Results.Problem(
                detail: "Execution block must carry at least one non-empty field on PUT. Use DELETE to clear the block.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        await store.SetAsync(actorId, defaults, cancellationToken);
        var stored = await store.GetAsync(actorId, cancellationToken);
        return Results.Ok(ToResponse(stored, runtimeRegistry));
    }

    private static async Task<IResult> ClearExecutionAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitExecutionStore store,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("unit", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        await store.ClearAsync(actorId, cancellationToken);
        return Results.NoContent();
    }

    internal static UnitExecutionResponse ToResponse(
        UnitExecutionDefaults? defaults,
        IAgentRuntimeRegistry runtimeRegistry)
    {
        if (defaults is null)
        {
            return new UnitExecutionResponse();
        }

        // #1732: derive ToolKind from the runtime registry. Returns null when
        // Agent is unset or names a runtime that is not registered here.
        string? toolKind = null;
        if (!string.IsNullOrWhiteSpace(defaults.Agent))
        {
            toolKind = runtimeRegistry.Get(defaults.Agent)?.ToolKind;
        }

        return new UnitExecutionResponse(
            Image: defaults.Image,
            Runtime: defaults.Runtime,
            Provider: defaults.Provider,
            Model: defaults.Model,
            Agent: defaults.Agent,
            ToolKind: toolKind);
    }
}