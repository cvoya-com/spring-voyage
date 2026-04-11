// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Host.Api.Models;

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
        var group = app.MapGroup("/api/v1/directory")
            .WithTags("Directory");

        group.MapGet("/", ListEntriesAsync)
            .WithName("ListDirectoryEntries")
            .WithSummary("List all directory entries");

        group.MapGet("/role/{role}", FindByRoleAsync)
            .WithName("FindByRole")
            .WithSummary("Find directory entries by role");

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

    private static DirectoryEntryResponse ToDirectoryEntryResponse(DirectoryEntry entry) =>
        new(
            new AddressDto(entry.Address.Scheme, entry.Address.Path),
            entry.ActorId,
            entry.DisplayName,
            entry.Description,
            entry.Role,
            entry.RegisteredAt);
}