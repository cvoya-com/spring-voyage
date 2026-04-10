// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Maps unit-related API endpoints.
/// </summary>
public static class UnitEndpoints
{
    /// <summary>
    /// Registers unit endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapUnitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/units")
            .WithTags("Units");

        group.MapGet("/", ListUnitsAsync)
            .WithName("ListUnits")
            .WithSummary("List all registered units");

        group.MapGet("/{id}", GetUnitAsync)
            .WithName("GetUnit")
            .WithSummary("Get unit details and members");

        group.MapPost("/", CreateUnitAsync)
            .WithName("CreateUnit")
            .WithSummary("Create a new unit");

        group.MapDelete("/{id}", DeleteUnitAsync)
            .WithName("DeleteUnit")
            .WithSummary("Delete a unit");

        group.MapPost("/{id}/members", AddMemberAsync)
            .WithName("AddMember")
            .WithSummary("Add a member to a unit");

        group.MapDelete("/{id}/members/{memberId}", RemoveMemberAsync)
            .WithName("RemoveMember")
            .WithSummary("Remove a member from a unit");

        return group;
    }

    private static async Task<IResult> ListUnitsAsync(
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var entries = await directoryService.ListAllAsync(cancellationToken);

        var units = entries
            .Where(e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            .Select(ToUnitResponse)
            .ToList();

        return Results.Ok(units);
    }

    private static async Task<IResult> GetUnitAsync(
        string id,
        IDirectoryService directoryService,
        MessageRouter messageRouter,
        CancellationToken cancellationToken)
    {
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Unit '{id}' not found" });
        }

        // Send a StatusQuery to get unit details including members.
        var statusQuery = new Message(
            Guid.NewGuid(),
            new Address("human", "api"),
            address,
            MessageType.StatusQuery,
            null,
            default,
            DateTimeOffset.UtcNow);

        var result = await messageRouter.RouteAsync(statusQuery, cancellationToken);

        if (!result.IsSuccess)
        {
            return Results.Ok(ToUnitResponse(entry));
        }

        return Results.Ok(new
        {
            Unit = ToUnitResponse(entry),
            Details = result.Value?.Payload
        });
    }

    private static async Task<IResult> CreateUnitAsync(
        CreateUnitRequest request,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var actorId = Guid.NewGuid().ToString();
        var address = new Address("unit", request.Name);
        var entry = new DirectoryEntry(
            address,
            actorId,
            request.DisplayName,
            request.Description,
            null,
            DateTimeOffset.UtcNow);

        await directoryService.RegisterAsync(entry, cancellationToken);

        return Results.Created($"/api/v1/units/{request.Name}", ToUnitResponse(entry));
    }

    private static async Task<IResult> DeleteUnitAsync(
        string id,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Unit '{id}' not found" });
        }

        await directoryService.UnregisterAsync(address, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> AddMemberAsync(
        string id,
        AddMemberRequest request,
        IDirectoryService directoryService,
        MessageRouter messageRouter,
        CancellationToken cancellationToken)
    {
        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Unit '{id}' not found" });
        }

        // Send a Domain message to the unit actor to add the member.
        var payload = JsonSerializer.SerializeToElement(new
        {
            Action = "AddMember",
            MemberScheme = request.MemberAddress.Scheme,
            MemberPath = request.MemberAddress.Path
        });

        var message = new Message(
            Guid.NewGuid(),
            new Address("human", "api"),
            unitAddress,
            MessageType.Domain,
            null,
            payload,
            DateTimeOffset.UtcNow);

        var result = await messageRouter.RouteAsync(message, cancellationToken);

        if (!result.IsSuccess)
        {
            return Results.Problem(
                detail: result.Error!.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Ok(new { Status = "Member added" });
    }

    private static async Task<IResult> RemoveMemberAsync(
        string id,
        string memberId,
        IDirectoryService directoryService,
        MessageRouter messageRouter,
        CancellationToken cancellationToken)
    {
        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Unit '{id}' not found" });
        }

        // Send a Domain message to the unit actor to remove the member.
        var payload = JsonSerializer.SerializeToElement(new
        {
            Action = "RemoveMember",
            MemberId = memberId
        });

        var message = new Message(
            Guid.NewGuid(),
            new Address("human", "api"),
            unitAddress,
            MessageType.Domain,
            null,
            payload,
            DateTimeOffset.UtcNow);

        var result = await messageRouter.RouteAsync(message, cancellationToken);

        if (!result.IsSuccess)
        {
            return Results.Problem(
                detail: result.Error!.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.NoContent();
    }

    private static UnitResponse ToUnitResponse(DirectoryEntry entry) =>
        new(
            entry.ActorId,
            entry.Address.Path,
            entry.DisplayName,
            entry.Description,
            entry.RegisteredAt);
}
