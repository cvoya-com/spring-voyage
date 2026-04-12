// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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

        group.MapPost("/{id}/start", StartUnitAsync)
            .WithName("StartUnit")
            .WithSummary("Start the runtime container for a unit");

        group.MapPost("/{id}/stop", StopUnitAsync)
            .WithName("StopUnit")
            .WithSummary("Stop the runtime container for a unit");

        group.MapPost("/{id}/members", AddMemberAsync)
            .WithName("AddMember")
            .WithSummary("Add a member to a unit");

        group.MapDelete("/{id}/members/{memberId}", RemoveMemberAsync)
            .WithName("RemoveMember")
            .WithSummary("Remove a member from a unit");

        group.MapPatch("/{id}/humans/{humanId}/permissions", SetHumanPermissionAsync)
            .WithName("SetHumanPermission")
            .WithSummary("Set permission level for a human within a unit")
            .RequireAuthorization(PermissionPolicies.UnitOwner);

        group.MapGet("/{id}/humans", GetHumanPermissionsAsync)
            .WithName("GetHumanPermissions")
            .WithSummary("Get all human permissions for a unit")
            .RequireAuthorization(PermissionPolicies.UnitViewer);

        return group;
    }

    private static async Task<IResult> ListUnitsAsync(
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var entries = await directoryService.ListAllAsync(cancellationToken);

        var units = entries
            .Where(e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            .Select(e => ToUnitResponse(e))
            .ToList();

        return Results.Ok(units);
    }

    private static async Task<IResult> GetUnitAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] MessageRouter messageRouter,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");

        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Unit '{id}' not found" });
        }

        var status = await TryGetUnitStatusAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);

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
            return Results.Ok(ToUnitResponse(entry, status));
        }

        return Results.Ok(new
        {
            Unit = ToUnitResponse(entry, status),
            Details = result.Value?.Payload
        });
    }

    private static async Task<UnitStatus> TryGetUnitStatusAsync(
        IActorProxyFactory actorProxyFactory,
        string actorId,
        ILogger logger,
        string unitId,
        CancellationToken cancellationToken)
    {
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(actorId), nameof(IUnitActor));
            return await proxy.GetStatusAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Non-fatal: the unit exists in the directory but its actor has not yet
            // persisted state (fresh registration) or is unreachable. Returning Draft
            // preserves the directory-first read path, but the failure must be visible.
            logger.LogWarning(ex,
                "Failed to read persisted status for unit {UnitId}; reporting Draft.",
                unitId);
            return UnitStatus.Draft;
        }
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

        // TODO: consider requiring Stopped before Delete — follow-up.
        await directoryService.UnregisterAsync(address, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> StartUnitAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitContainerLifecycle containerLifecycle,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Unit '{id}' not found" });
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(IUnitActor));

        var startingTransition = await proxy.TransitionAsync(UnitStatus.Starting, cancellationToken);
        if (!startingTransition.Success)
        {
            return Results.Conflict(new
            {
                Error = startingTransition.RejectionReason,
                CurrentStatus = startingTransition.CurrentStatus
            });
        }

        // TODO(#81 follow-up): Register GitHub webhooks during unit /start.
        try
        {
            await containerLifecycle.StartUnitAsync(entry.ActorId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Container start failed for unit {UnitId} (actor {ActorId}). Transitioning to Error.",
                id, entry.ActorId);

            var errorTransition = await proxy.TransitionAsync(UnitStatus.Error, cancellationToken);

            return Results.Problem(
                title: "Unit start failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>
                {
                    ["unitId"] = id,
                    ["currentStatus"] = errorTransition.CurrentStatus.ToString()
                });
        }

        var runningTransition = await proxy.TransitionAsync(UnitStatus.Running, cancellationToken);
        if (!runningTransition.Success)
        {
            logger.LogError(
                "Unit {UnitId} failed to transition to Running: {Reason}. Current status {Status}.",
                id, runningTransition.RejectionReason, runningTransition.CurrentStatus);

            return Results.Problem(
                title: "Unit start failed",
                detail: runningTransition.RejectionReason,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Accepted($"/api/v1/units/{id}", new
        {
            UnitId = id,
            Status = runningTransition.CurrentStatus.ToString()
        });
    }

    private static async Task<IResult> StopUnitAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitContainerLifecycle containerLifecycle,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Unit '{id}' not found" });
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(IUnitActor));

        var stoppingTransition = await proxy.TransitionAsync(UnitStatus.Stopping, cancellationToken);
        if (!stoppingTransition.Success)
        {
            return Results.Conflict(new
            {
                Error = stoppingTransition.RejectionReason,
                CurrentStatus = stoppingTransition.CurrentStatus
            });
        }

        try
        {
            await containerLifecycle.StopUnitAsync(entry.ActorId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Container stop failed for unit {UnitId} (actor {ActorId}). Transitioning to Error.",
                id, entry.ActorId);

            var errorTransition = await proxy.TransitionAsync(UnitStatus.Error, cancellationToken);

            return Results.Problem(
                title: "Unit stop failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>
                {
                    ["unitId"] = id,
                    ["currentStatus"] = errorTransition.CurrentStatus.ToString()
                });
        }

        var stoppedTransition = await proxy.TransitionAsync(UnitStatus.Stopped, cancellationToken);
        if (!stoppedTransition.Success)
        {
            logger.LogError(
                "Unit {UnitId} failed to transition to Stopped: {Reason}. Current status {Status}.",
                id, stoppedTransition.RejectionReason, stoppedTransition.CurrentStatus);

            return Results.Problem(
                title: "Unit stop failed",
                detail: stoppedTransition.RejectionReason,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Accepted($"/api/v1/units/{id}", new
        {
            UnitId = id,
            Status = stoppedTransition.CurrentStatus.ToString()
        });
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

    private static async Task<IResult> SetHumanPermissionAsync(
        string id,
        string humanId,
        SetHumanPermissionRequest request,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Unit '{id}' not found" });
        }

        if (!Enum.TryParse<PermissionLevel>(request.Permission, ignoreCase: true, out var permissionLevel))
        {
            return Results.BadRequest(new { Error = $"Invalid permission level: '{request.Permission}'" });
        }

        var permissionEntry = new UnitPermissionEntry(
            humanId,
            permissionLevel,
            request.Identity,
            request.Notifications ?? true);

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(IUnitActor));

        await unitProxy.SetHumanPermissionAsync(humanId, permissionEntry, cancellationToken);

        // Also update the human actor's unit-scoped permission map.
        var humanProxy = actorProxyFactory.CreateActorProxy<IHumanActor>(
            new ActorId(humanId), nameof(IHumanActor));

        await humanProxy.SetPermissionForUnitAsync(id, permissionLevel, cancellationToken);

        return Results.Ok(new { HumanId = humanId, Permission = permissionLevel.ToString() });
    }

    private static async Task<IResult> GetHumanPermissionsAsync(
        string id,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Unit '{id}' not found" });
        }

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(IUnitActor));

        var permissions = await unitProxy.GetHumanPermissionsAsync(cancellationToken);

        return Results.Ok(permissions);
    }

    private static UnitResponse ToUnitResponse(DirectoryEntry entry, UnitStatus status = UnitStatus.Draft) =>
        new(
            entry.ActorId,
            entry.Address.Path,
            entry.DisplayName,
            entry.Description,
            entry.RegisteredAt,
            status);
}