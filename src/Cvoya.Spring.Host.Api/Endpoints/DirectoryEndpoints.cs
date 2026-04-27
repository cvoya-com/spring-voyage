// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps directory-related API endpoints.
/// </summary>
public static class DirectoryEndpoints
{
    /// <summary>
    /// Registers directory endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/directory")
            .WithTags("Directory");

        group.MapGet("/", ListEntriesAsync)
            .WithName("ListDirectoryEntries")
            .WithSummary("List all directory entries")
            .Produces<DirectoryEntryResponse[]>(StatusCodes.Status200OK);

        group.MapGet("/role/{role}", FindByRoleAsync)
            .WithName("FindByRole")
            .WithSummary("Find directory entries by role")
            .Produces<DirectoryEntryResponse[]>(StatusCodes.Status200OK);

        group.MapPost("/search", SearchAsync)
            .WithName("SearchDirectory")
            .WithSummary("Search the expertise directory (#542)")
            .WithDescription(
                "Free-text + structured search over the expertise directory. " +
                "Outside a unit boundary, only projected entries are returned; " +
                "inside, callers see the full aggregated scope. Step 1 is lexical " +
                "/ full-text — semantic search is tracked as a follow-up.")
            .Produces<DirectorySearchResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return group;
    }

    private static async Task<IResult> ListEntriesAsync(
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var entries = await directoryService.ListAllAsync(cancellationToken);

        var response = entries
            .Select(ToDirectoryEntryResponse)
            .ToList();

        return Results.Ok(response);
    }

    private static async Task<IResult> FindByRoleAsync(
        string role,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var entries = await directoryService.ResolveByRoleAsync(role, cancellationToken);

        var response = entries
            .Select(ToDirectoryEntryResponse)
            .ToList();

        return Results.Ok(response);
    }

    private static async Task<IResult> SearchAsync(
        [FromBody] DirectorySearchRequest? request,
        [FromServices] IExpertiseSearch search,
        CancellationToken cancellationToken)
    {
        // A null body is equivalent to "search everything" — treat it as an
        // empty query. Clients that accidentally POST no body should not be
        // punished with a 400; the server-side clamps on limit / offset keep
        // the request cheap.
        request ??= new DirectorySearchRequest();

        if (request.Limit < 0 || request.Offset < 0)
        {
            return Results.Problem(
                detail: "Limit and Offset must be non-negative.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var query = new ExpertiseSearchQuery(
            Text: request.Text,
            Owner: request.Owner is null ? null : new Address(request.Owner.Scheme, request.Owner.Path),
            Domains: request.Domains,
            TypedOnly: request.TypedOnly,
            Caller: request.Caller is null ? null : new Address(request.Caller.Scheme, request.Caller.Path),
            Context: request.InsideUnit ? BoundaryViewContext.InsideUnit : BoundaryViewContext.External,
            Limit: request.Limit <= 0 ? ExpertiseSearchQuery.DefaultLimit : request.Limit,
            Offset: request.Offset);

        var result = await search.SearchAsync(query, cancellationToken);

        return Results.Ok(new DirectorySearchResponse(
            result.Hits.Select(ToHitResponse).ToList(),
            result.TotalCount,
            result.Limit,
            result.Offset));
    }

    private static DirectoryEntryResponse ToDirectoryEntryResponse(DirectoryEntry entry) =>
        new(
            new AddressDto(entry.Address.Scheme, entry.Address.Path),
            entry.ActorId,
            entry.DisplayName,
            entry.Description,
            entry.Role,
            entry.RegisteredAt);

    private static DirectorySearchHitResponse ToHitResponse(ExpertiseSearchHit hit)
    {
        // The ancestor chain + projection paths are additive (#553). Both
        // fields land as empty collections (not null) for direct hits so
        // generated clients can treat "no chain" and "empty chain"
        // identically without a null check.
        var chain = hit.AncestorChain is { Count: > 0 }
            ? hit.AncestorChain.Select(a => new AddressDto(a.Scheme, a.Path)).ToList()
            : (IReadOnlyList<AddressDto>)Array.Empty<AddressDto>();
        var paths = hit.ProjectionPaths is { Count: > 0 }
            ? hit.ProjectionPaths.ToList()
            : (IReadOnlyList<string>)Array.Empty<string>();

        return new DirectorySearchHitResponse(
            hit.Slug,
            new ExpertiseDomainDto(
                hit.Domain.Name,
                hit.Domain.Description,
                hit.Domain.Level?.ToString().ToLowerInvariant()),
            new AddressDto(hit.Owner.Scheme, hit.Owner.Path),
            hit.OwnerDisplayName,
            hit.AggregatingUnit is null
                ? null
                : new AddressDto(hit.AggregatingUnit.Scheme, hit.AggregatingUnit.Path),
            hit.TypedContract,
            hit.Score,
            hit.MatchReason,
            chain,
            paths);
    }
}