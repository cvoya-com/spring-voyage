// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Http;
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

        // ADR-0039 §9: the PUT handler hand-reads the body as
        // JsonDocument so it can reject the legacy `containerRuntime`
        // key with a structured 400 before model-binding silently drops
        // it. The DTO surface stays advertised via Accepts<T>() so the
        // OpenAPI schema (and downstream Kiota client) keeps a typed
        // body.
        group.MapPut("/", SetExecutionAsync)
            .WithName("SetUnitExecution")
            .WithSummary("Upsert one or more fields on the unit's execution defaults (partial update)")
            .Accepts<UnitExecutionResponse>("application/json")
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
        var defaults = await store.GetAsync(actorId, cancellationToken);
        return Results.Ok(ToResponse(defaults));
    }

    private static async Task<IResult> SetExecutionAsync(
        string id,
        HttpContext httpContext,
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

        // ADR-0039 §7 / §9: read the body as a JsonDocument first so we can
        // reject the removed `containerRuntime` key with a structured 400
        // before model-binding silently drops it. Legacy clients still in
        // the field need an actionable error, not a no-op success.
        var jsonOptions = httpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value
            .SerializerOptions;

        UnitExecutionResponse? request;
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            return Results.Problem(
                detail: $"Request body is not valid JSON: {ex.Message}",
                statusCode: StatusCodes.Status400BadRequest);
        }

        using (document)
        {
            var legacy = LegacyExecutionFieldProblems
                .LegacyContainerRuntimeFieldOrNull(document.RootElement);
            if (legacy is not null)
            {
                return legacy;
            }

            try
            {
                request = document.Deserialize<UnitExecutionResponse>(jsonOptions);
            }
            catch (JsonException ex)
            {
                return Results.Problem(
                    detail: $"Request body could not be deserialized: {ex.Message}",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        request ??= new UnitExecutionResponse();

        // ADR-0038: the wire shape carries `runtime` (agent-runtime
        // catalogue id) and structured `model: {provider, id}`. The
        // store still names that catalogue slot `Agent`, so the
        // wire-domain mapping happens here. ADR-0039 §7 removed the
        // per-config container-runtime selector; host configuration owns
        // docker/podman selection.
        var defaults = new UnitExecutionDefaults(
            Image: request.Image,
            Provider: request.Model?.Provider,
            Model: request.Model?.Id,
            Agent: request.Runtime);

        if (defaults.IsEmpty)
        {
            return Results.Problem(
                detail: "Execution block must carry at least one non-empty field on PUT. Use DELETE to clear the block.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        await store.SetAsync(actorId, defaults, cancellationToken);
        var stored = await store.GetAsync(actorId, cancellationToken);
        return Results.Ok(ToResponse(stored));
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

    internal static UnitExecutionResponse ToResponse(UnitExecutionDefaults? defaults)
    {
        if (defaults is null)
        {
            return new UnitExecutionResponse();
        }

        // ADR-0038: project the internal store fields onto the new wire
        // shape — `runtime` (catalogue id) and structured
        // `model: {provider, id}`. ADR-0039 §7 drops the `containerRuntime`
        // slot from the wire surface; the internal store still carries it
        // for back-compat (until G8) but it is never emitted on the wire.
        AiModelDto? model = null;
        if (!string.IsNullOrWhiteSpace(defaults.Model) && !string.IsNullOrWhiteSpace(defaults.Provider))
        {
            model = new AiModelDto(defaults.Provider!, defaults.Model!);
        }

        return new UnitExecutionResponse(
            Image: defaults.Image,
            Runtime: defaults.Agent,
            Model: model);
    }
}