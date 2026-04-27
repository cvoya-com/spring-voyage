// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps the memory-inspector read API introduced in SVR-memories (plan
/// §4 / §13). Two endpoints mirror the Explorer's Memory tab:
/// <c>GET /api/v1/units/{id}/memories</c> and
/// <c>GET /api/v1/agents/{id}/memories</c>. Both return empty short-term +
/// long-term lists in v2.0 — the real backing store ships in
/// <c>V21-memory-write</c>.
/// </summary>
public static class MemoriesEndpoints
{
    /// <summary>
    /// Registers the memory endpoints. Call from <c>Program.cs</c> after
    /// <c>MapTenantTreeEndpoints</c>. Returns a single
    /// <see cref="RouteGroupBuilder"/> so callers can apply
    /// <c>RequireAuthorization()</c> uniformly.
    /// </summary>
    public static RouteGroupBuilder MapMemoriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(string.Empty);

        group.MapGet("/api/v1/tenant/units/{id}/memories", GetUnitMemoriesAsync)
            .WithTags("Units")
            .WithName("GetUnitMemories")
            .WithSummary("Read the unit's short-term and long-term memory entries (stub-empty in v2.0)")
            .Produces<MemoriesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/api/v1/tenant/agents/{id}/memories", GetAgentMemoriesAsync)
            .WithTags("Agents")
            .WithName("GetAgentMemories")
            .WithSummary("Read the agent's short-term and long-term memory entries (stub-empty in v2.0)")
            .Produces<MemoriesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> GetUnitMemoriesAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        CancellationToken cancellationToken)
        => await ResolveAndReturnEmptyAsync("unit", id, directoryService, cancellationToken);

    private static async Task<IResult> GetAgentMemoriesAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        CancellationToken cancellationToken)
        => await ResolveAndReturnEmptyAsync("agent", id, directoryService, cancellationToken);

    private static async Task<IResult> ResolveAndReturnEmptyAsync(
        string scheme,
        string id,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address(scheme, id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"{char.ToUpperInvariant(scheme[0])}{scheme[1..]} '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new MemoriesResponse(
            ShortTerm: Array.Empty<MemoryEntry>(),
            LongTerm: Array.Empty<MemoryEntry>()));
    }
}