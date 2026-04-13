// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

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

        group.MapPost("/from-yaml", CreateUnitFromYamlAsync)
            .WithName("CreateUnitFromYaml")
            .WithSummary("Create a unit by applying a raw unit manifest YAML document");

        group.MapPost("/from-template", CreateUnitFromTemplateAsync)
            .WithName("CreateUnitFromTemplate")
            .WithSummary("Create a unit from one of the templates listed by /api/v1/packages/templates");

        group.MapPatch("/{id}", UpdateUnitAsync)
            .WithName("UpdateUnit")
            .WithSummary("Update mutable unit metadata (displayName, description, model, color)");

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

        group.MapPut("/{id}/github", SetGitHubConfigAsync)
            .WithName("SetUnitGitHubConfig")
            .WithSummary("Configure the GitHub repository a unit is bound to");

        group.MapDelete("/{id}/github", ClearGitHubConfigAsync)
            .WithName("ClearUnitGitHubConfig")
            .WithSummary("Clear the unit's GitHub repository binding");

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
        var metadata = await TryGetUnitMetadataAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);

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
            return Results.Ok(ToUnitResponse(entry, status, metadata));
        }

        return Results.Ok(new
        {
            Unit = ToUnitResponse(entry, status, metadata),
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

    private static async Task<UnitMetadata> TryGetUnitMetadataAsync(
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
            return await proxy.GetMetadataAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Non-fatal: a fresh unit may not have any metadata persisted yet,
            // or the actor may be transiently unreachable. Returning an empty
            // record keeps the read path working but the failure must be visible.
            logger.LogWarning(ex,
                "Failed to read persisted metadata for unit {UnitId}; reporting empty metadata.",
                unitId);
            return new UnitMetadata(null, null, null, null);
        }
    }

    private static async Task<IResult> CreateUnitAsync(
        CreateUnitRequest request,
        [FromServices] IUnitCreationService creationService,
        CancellationToken cancellationToken)
    {
        var result = await creationService.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/v1/units/{request.Name}", result.Unit);
    }

    private static async Task<IResult> CreateUnitFromYamlAsync(
        CreateUnitFromYamlRequest request,
        [FromServices] IUnitCreationService creationService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Yaml))
        {
            return Results.BadRequest(new { Error = "Request body must include non-empty 'yaml'." });
        }

        UnitManifest manifest;
        try
        {
            manifest = ManifestParser.Parse(request.Yaml);
        }
        catch (ManifestParseException ex)
        {
            return Results.BadRequest(new { Error = ex.Message });
        }

        var overrides = new UnitCreationOverrides(request.DisplayName, request.Color, request.Model);
        var result = await creationService.CreateFromManifestAsync(manifest, overrides, cancellationToken);

        return Results.Created(
            $"/api/v1/units/{result.Unit.Name}",
            new UnitCreationResponse(result.Unit, result.Warnings, result.MembersAdded));
    }

    private static async Task<IResult> CreateUnitFromTemplateAsync(
        CreateUnitFromTemplateRequest request,
        [FromServices] IPackageCatalogService catalog,
        [FromServices] IUnitCreationService creationService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Package) || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { Error = "Request body must include both 'package' and 'name'." });
        }

        var yaml = await catalog.LoadUnitTemplateYamlAsync(request.Package, request.Name, cancellationToken);
        if (yaml is null)
        {
            return Results.NotFound(new
            {
                Error = $"Template '{request.Package}/{request.Name}' was not found.",
            });
        }

        UnitManifest manifest;
        try
        {
            manifest = ManifestParser.Parse(yaml);
        }
        catch (ManifestParseException ex)
        {
            return Results.Problem(
                title: "Template YAML is invalid",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var overrides = new UnitCreationOverrides(request.DisplayName, request.Color, request.Model);
        var result = await creationService.CreateFromManifestAsync(manifest, overrides, cancellationToken);

        return Results.Created(
            $"/api/v1/units/{result.Unit.Name}",
            new UnitCreationResponse(result.Unit, result.Warnings, result.MembersAdded));
    }

    private static async Task<IResult> UpdateUnitAsync(
        string id,
        UpdateUnitRequest request,
        [FromServices] IDirectoryService directoryService,
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

        // DisplayName/Description live on the directory entity — route those
        // through IDirectoryService (#123). Model/Color are actor-owned and
        // persisted through SetMetadataAsync. We always forward the PATCH to
        // the actor so the audit trail captures the change even when only
        // directory-side fields are touched.
        if (request.DisplayName is not null || request.Description is not null)
        {
            var updatedEntry = await directoryService.UpdateEntryAsync(
                address, request.DisplayName, request.Description, cancellationToken);

            entry = updatedEntry ?? entry;
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(IUnitActor));

        var metadata = new UnitMetadata(
            DisplayName: request.DisplayName,
            Description: request.Description,
            Model: request.Model,
            Color: request.Color);

        await proxy.SetMetadataAsync(metadata, cancellationToken);

        var status = await TryGetUnitStatusAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);
        var updatedMetadata = await TryGetUnitMetadataAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);

        return Results.Ok(ToUnitResponse(entry, status, updatedMetadata));
    }

    private static async Task<IResult> DeleteUnitAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
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

        // Gate deletion on lifecycle status (#116). Allowing DELETE while the unit is
        // Running/Starting/Stopping leaves the container, sidecar, and network orphaned.
        // Only Draft (never started) and Stopped (cleanly torn down) are safe. Error-state
        // units still require an explicit /stop to drive them to Stopped first; a future
        // force-delete flag can cover unrecoverable Error cases.
        var status = await TryGetUnitStatusAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);

        if (status != UnitStatus.Draft && status != UnitStatus.Stopped)
        {
            return Results.Conflict(new
            {
                Error = $"Unit '{id}' is {status}; stop it before deleting.",
                CurrentStatus = status.ToString(),
                Hint = $"POST /api/v1/units/{id}/stop",
            });
        }

        await directoryService.UnregisterAsync(address, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> StartUnitAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitContainerLifecycle containerLifecycle,
        [FromServices] IGitHubWebhookRegistrar webhookRegistrar,
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

        // Register a GitHub webhook on the configured repo, if any. Failure here is not
        // fatal to the unit itself — the container is up and the platform will still
        // route inbound events from any pre-existing hook — but we must surface it so
        // an operator can act. Hook id is persisted on the unit so /stop can delete it.
        var githubConfig = await proxy.GetGitHubConfigAsync(cancellationToken);
        if (githubConfig is not null)
        {
            try
            {
                var hookId = await webhookRegistrar.RegisterAsync(
                    githubConfig.Owner, githubConfig.Repo, cancellationToken);
                await proxy.SetGitHubHookIdAsync(hookId, cancellationToken);
                logger.LogInformation(
                    "Registered GitHub webhook {HookId} for unit {UnitId} on {Owner}/{Repo}",
                    hookId, id, githubConfig.Owner, githubConfig.Repo);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to register GitHub webhook for unit {UnitId} on {Owner}/{Repo}. Proceeding to Running; events will not flow until the hook is created manually.",
                    id, githubConfig.Owner, githubConfig.Repo);
            }
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
        [FromServices] IGitHubWebhookRegistrar webhookRegistrar,
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

        // Tear down the GitHub webhook that /start created, if any. A stale
        // hook id (404) is logged and swallowed inside the registrar so /stop
        // can complete even when the hook was deleted out of band.
        var githubConfig = await proxy.GetGitHubConfigAsync(cancellationToken);
        var hookId = await proxy.GetGitHubHookIdAsync(cancellationToken);
        if (githubConfig is not null && hookId is not null)
        {
            try
            {
                await webhookRegistrar.UnregisterAsync(
                    githubConfig.Owner, githubConfig.Repo, hookId.Value, cancellationToken);
                await proxy.SetGitHubHookIdAsync(null, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to delete GitHub webhook {HookId} for unit {UnitId} on {Owner}/{Repo}. Continuing teardown; the hook id remains persisted so operators can retry.",
                    hookId, id, githubConfig.Owner, githubConfig.Repo);
            }
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

    private static async Task<IResult> SetGitHubConfigAsync(
        string id,
        SetUnitGitHubConfigRequest request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Owner) || string.IsNullOrWhiteSpace(request.Repo))
        {
            return Results.BadRequest(new { Error = "Both 'Owner' and 'Repo' are required." });
        }

        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Unit '{id}' not found" });
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(IUnitActor));

        var config = new UnitGitHubConfig(request.Owner, request.Repo);
        await proxy.SetGitHubConfigAsync(config, cancellationToken);

        return Results.Ok(new
        {
            UnitId = id,
            GitHub = config,
        });
    }

    private static async Task<IResult> ClearGitHubConfigAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Unit '{id}' not found" });
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(IUnitActor));

        await proxy.SetGitHubConfigAsync(null, cancellationToken);

        return Results.NoContent();
    }

    private static UnitResponse ToUnitResponse(
        DirectoryEntry entry,
        UnitStatus status = UnitStatus.Draft,
        UnitMetadata? metadata = null) =>
        new(
            entry.ActorId,
            entry.Address.Path,
            entry.DisplayName,
            entry.Description,
            entry.RegisteredAt,
            status,
            metadata?.Model,
            metadata?.Color);
}