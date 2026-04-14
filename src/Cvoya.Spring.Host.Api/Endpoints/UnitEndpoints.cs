// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
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
            .WithSummary("List all registered units")
            .Produces<UnitResponse[]>(StatusCodes.Status200OK);

        group.MapGet("/{id}", GetUnitAsync)
            .WithName("GetUnit")
            .WithSummary("Get unit details and members")
            .Produces<UnitDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateUnitAsync)
            .WithName("CreateUnit")
            .WithSummary("Create a new unit")
            .Produces<UnitResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/from-yaml", CreateUnitFromYamlAsync)
            .WithName("CreateUnitFromYaml")
            .WithSummary("Create a unit by applying a raw unit manifest YAML document")
            .Produces<UnitCreationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/from-template", CreateUnitFromTemplateAsync)
            .WithName("CreateUnitFromTemplate")
            .WithSummary("Create a unit from one of the templates listed by /api/v1/packages/templates")
            .Produces<UnitCreationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{id}", UpdateUnitAsync)
            .WithName("UpdateUnit")
            .WithSummary("Update mutable unit metadata (displayName, description, model, color)")
            .Produces<UnitResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}", DeleteUnitAsync)
            .WithName("DeleteUnit")
            .WithSummary("Delete a unit")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id}/start", StartUnitAsync)
            .WithName("StartUnit")
            .WithSummary("Start the runtime container for a unit")
            .Produces<UnitLifecycleResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id}/stop", StopUnitAsync)
            .WithName("StopUnit")
            .WithSummary("Stop the runtime container for a unit")
            .Produces<UnitLifecycleResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id}/members", AddMemberAsync)
            .WithName("AddMember")
            .WithSummary("Add a member to a unit")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id}/members/{memberId}", RemoveMemberAsync)
            .WithName("RemoveMember")
            .WithSummary("Remove a member from a unit")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Generic (non-polymorphic) pointer endpoints — the typed per-unit
        // config lives on the connector package's own surface under
        // /api/v1/connectors/{slug}/units/{unitId}/config.
        group.MapUnitConnectorPointerEndpoints();

        group.MapPatch("/{id}/humans/{humanId}/permissions", SetHumanPermissionAsync)
            .WithName("SetHumanPermission")
            .WithSummary("Set permission level for a human within a unit")
            .RequireAuthorization(PermissionPolicies.UnitOwner)
            .Produces<SetHumanPermissionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/humans", GetHumanPermissionsAsync)
            .WithName("GetHumanPermissions")
            .WithSummary("Get all human permissions for a unit")
            .RequireAuthorization(PermissionPolicies.UnitViewer)
            .Produces<IReadOnlyList<UnitPermissionEntry>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/agents", ListUnitAgentsAsync)
            .WithName("ListUnitAgents")
            .WithSummary("List the agents that belong to this unit (members with scheme=agent), enriched with each agent's metadata")
            .Produces<AgentResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/agents/{agentId}", AssignUnitAgentAsync)
            .WithName("AssignUnitAgent")
            .WithSummary("Assign an agent to this unit. Creates a membership row (M:N per #160) and adds the agent to the unit's members list; no conflict is raised if the agent is also a member of another unit.")
            .Produces<AgentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}/agents/{agentId}", UnassignUnitAgentAsync)
            .WithName("UnassignUnitAgent")
            .WithSummary("Unassign an agent from this unit. Deletes the membership row and removes the agent from the unit's members list; other memberships the agent holds are unaffected.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

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
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
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

        var unitResponse = ToUnitResponse(entry, status, metadata);
        if (!result.IsSuccess)
        {
            return Results.Ok(new UnitDetailResponse(unitResponse, null));
        }

        return Results.Ok(new UnitDetailResponse(unitResponse, result.Value?.Payload));
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
        try
        {
            var result = await creationService.CreateAsync(request, cancellationToken);
            return Results.Created($"/api/v1/units/{request.Name}", result.Unit);
        }
        catch (UnitCreationBindingException ex)
        {
            return ProblemFromBindingFailure(ex);
        }
    }

    private static async Task<IResult> CreateUnitFromYamlAsync(
        CreateUnitFromYamlRequest request,
        [FromServices] IUnitCreationService creationService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Yaml))
        {
            return Results.Problem(detail: "Request body must include non-empty 'yaml'.", statusCode: StatusCodes.Status400BadRequest);
        }

        UnitManifest manifest;
        try
        {
            manifest = ManifestParser.Parse(request.Yaml);
        }
        catch (ManifestParseException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        var overrides = new UnitCreationOverrides(request.DisplayName, request.Color, request.Model);
        try
        {
            var result = await creationService.CreateFromManifestAsync(
                manifest, overrides, cancellationToken, request.Connector);

            return Results.Created(
                $"/api/v1/units/{result.Unit.Name}",
                new UnitCreationResponse(result.Unit, result.Warnings, result.MembersAdded));
        }
        catch (UnitCreationBindingException ex)
        {
            return ProblemFromBindingFailure(ex);
        }
        catch (SkillBundlePackageNotFoundException ex)
        {
            return Results.Problem(title: "Unknown skill package", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (SkillBundleNotFoundException ex)
        {
            return Results.Problem(title: "Unknown skill", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (SkillBundleValidationException ex)
        {
            return Results.Problem(title: "Skill bundle validation failed", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> CreateUnitFromTemplateAsync(
        CreateUnitFromTemplateRequest request,
        [FromServices] IPackageCatalogService catalog,
        [FromServices] IUnitCreationService creationService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Package) || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.Problem(detail: "Request body must include both 'package' and 'name'.", statusCode: StatusCodes.Status400BadRequest);
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
        try
        {
            var result = await creationService.CreateFromManifestAsync(
                manifest, overrides, cancellationToken, request.Connector);

            return Results.Created(
                $"/api/v1/units/{result.Unit.Name}",
                new UnitCreationResponse(result.Unit, result.Warnings, result.MembersAdded));
        }
        catch (UnitCreationBindingException ex)
        {
            return ProblemFromBindingFailure(ex);
        }
        catch (SkillBundlePackageNotFoundException ex)
        {
            return Results.Problem(title: "Unknown skill package", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (SkillBundleNotFoundException ex)
        {
            return Results.Problem(title: "Unknown skill", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (SkillBundleValidationException ex)
        {
            return Results.Problem(title: "Skill bundle validation failed", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// Maps <see cref="UnitCreationBindingException"/> outcomes onto the
    /// ProblemDetails conventions established by #192. The service has
    /// already rolled back the partial unit by the time we get here, so
    /// the client sees a clean 4xx / 502 with no residual state.
    /// </summary>
    private static IResult ProblemFromBindingFailure(UnitCreationBindingException ex)
    {
        var status = ex.Reason switch
        {
            UnitCreationBindingFailureReason.UnknownConnectorType => StatusCodes.Status404NotFound,
            UnitCreationBindingFailureReason.InvalidBindingRequest => StatusCodes.Status400BadRequest,
            UnitCreationBindingFailureReason.StoreFailure => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status400BadRequest,
        };
        var title = ex.Reason switch
        {
            UnitCreationBindingFailureReason.UnknownConnectorType => "Unknown connector type",
            UnitCreationBindingFailureReason.InvalidBindingRequest => "Invalid connector binding",
            UnitCreationBindingFailureReason.StoreFailure => "Connector binding failed",
            _ => "Invalid connector binding",
        };
        return Results.Problem(title: title, detail: ex.Message, statusCode: status);
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
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
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
        [FromQuery] bool? force,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitContainerLifecycle containerLifecycle,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        [FromServices] IActivityEventBus activityEventBus,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // Gate deletion on lifecycle status (#116). Allowing DELETE while the unit is
        // Running/Starting/Stopping leaves the container, sidecar, and network orphaned.
        // Only Draft (never started) and Stopped (cleanly torn down) are safe.
        // Force-delete (#147) bypasses this gate to recover from stuck Error states
        // where /stop itself may fail or hang.
        var status = await TryGetUnitStatusAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);
        var isForce = force == true;

        if (!isForce && status != UnitStatus.Draft && status != UnitStatus.Stopped)
        {
            return Results.Conflict(new
            {
                Error = $"Unit '{id}' is {status}; stop it before deleting.",
                CurrentStatus = status,
                Hint = $"POST /api/v1/units/{id}/stop",
                ForceHint = $"DELETE /api/v1/units/{id}?force=true bypasses the gate for stuck units.",
            });
        }

        if (!isForce || status == UnitStatus.Draft || status == UnitStatus.Stopped)
        {
            // Clean-path delete. No runtime teardown required — the gate above
            // already proved the container / sidecar / webhook are either gone
            // or never existed.
            await directoryService.UnregisterAsync(address, cancellationToken);
            return Results.NoContent();
        }

        return await ForceDeleteUnitAsync(
            id, address, entry.ActorId, status,
            directoryService, actorProxyFactory,
            containerLifecycle, connectorTypes, activityEventBus,
            logger, cancellationToken);
    }

    /// <summary>
    /// Best-effort teardown for a unit that cannot transition through the normal
    /// /stop → /delete path. Each subsystem is torn down independently — a failure
    /// in one step is logged and recorded but does not block the others, so a
    /// broken sidecar can't prevent removal of an already-gone container. The
    /// directory entry is always removed last so the unit disappears from the API
    /// regardless of downstream state.
    /// </summary>
    private static async Task<IResult> ForceDeleteUnitAsync(
        string id,
        Address address,
        string actorId,
        UnitStatus previousStatus,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IUnitContainerLifecycle containerLifecycle,
        IEnumerable<IConnectorType> connectorTypes,
        IActivityEventBus activityEventBus,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Force-delete requested for unit {UnitId} in status {Status}. Performing best-effort teardown.",
            id, previousStatus);

        var failures = new List<string>();
        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(actorId), nameof(IUnitActor));

        try
        {
            // Delegate to the connector type owning this unit so it can
            // tear down its external resources. Each connector's stop hook
            // is responsible for catching its own errors; the try/catch
            // here is a second safety net.
            await DispatchConnectorStopAsync(id, proxy, connectorTypes, logger, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Force-delete: connector teardown failed for unit {UnitId}.", id);
            failures.Add("connector");
        }

        try
        {
            await containerLifecycle.StopUnitAsync(actorId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Force-delete: container teardown failed for unit {UnitId}.", id);
            failures.Add("container");
        }

        try
        {
            await directoryService.UnregisterAsync(address, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Force-delete: directory unregister failed for unit {UnitId}.", id);
            failures.Add("directory");
        }

        await PublishForceDeleteEventAsync(activityEventBus, address, previousStatus, failures, logger, cancellationToken);

        if (failures.Count > 0)
        {
            return Results.Ok(new UnitForceDeleteResponse(
                UnitId: id,
                ForceDeleted: true,
                PreviousStatus: previousStatus,
                TeardownFailures: failures,
                Message: "Directory entry removed; some teardown steps failed — inspect operator logs and the activity stream."));
        }

        return Results.NoContent();
    }

    private static async Task PublishForceDeleteEventAsync(
        IActivityEventBus bus,
        Address unit,
        UnitStatus previousStatus,
        IReadOnlyList<string> failures,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                previousStatus = previousStatus.ToString(),
                teardownFailures = failures,
            }));

            var severity = failures.Count > 0 ? ActivitySeverity.Warning : ActivitySeverity.Info;
            var summary = failures.Count > 0
                ? $"Force-deleted unit '{unit.Path}' (was {previousStatus}); {failures.Count} teardown step(s) failed."
                : $"Force-deleted unit '{unit.Path}' (was {previousStatus}).";

            var evt = new ActivityEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                unit,
                ActivityEventType.StateChanged,
                severity,
                summary,
                doc.RootElement.Clone());

            await bus.PublishAsync(evt, cancellationToken);
        }
        catch (Exception ex)
        {
            // Activity publication is observability only — log and swallow so a
            // bus failure never converts a successful force-delete into a 500.
            logger.LogWarning(ex,
                "Failed to publish force-delete activity event for unit {Unit}.",
                unit.Path);
        }
    }

    private static async Task<IResult> StartUnitAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitContainerLifecycle containerLifecycle,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
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
                    ["currentStatus"] = errorTransition.CurrentStatus
                });
        }

        // Dispatch connector start-hooks so each connector can provision
        // any external-system resources its binding needs (e.g. GitHub
        // webhooks). Each connector is responsible for catching its own
        // failures — we never let a misbehaving connector fail a unit start.
        await DispatchConnectorStartAsync(
            id, proxy, connectorTypes, logger, cancellationToken);

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

        return Results.Accepted(
            $"/api/v1/units/{id}",
            new UnitLifecycleResponse(id, runningTransition.CurrentStatus));
    }

    private static async Task<IResult> StopUnitAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitContainerLifecycle containerLifecycle,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
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

        // Dispatch connector stop-hooks so each connector can tear down any
        // external-system resources it provisioned on /start. Individual
        // connector failures are logged inside the connector and must not
        // block the /stop flow.
        await DispatchConnectorStopAsync(
            id, proxy, connectorTypes, logger, cancellationToken);

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
                    ["currentStatus"] = errorTransition.CurrentStatus
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

        return Results.Accepted(
            $"/api/v1/units/{id}",
            new UnitLifecycleResponse(id, stoppedTransition.CurrentStatus));
    }

    private static async Task<IResult> AddMemberAsync(
        string id,
        AddMemberRequest request,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var memberAddress = new Address(request.MemberAddress.Scheme, request.MemberAddress.Path);

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(IUnitActor));

        try
        {
            await unitProxy.AddMemberAsync(memberAddress, cancellationToken);
        }
        catch (CyclicMembershipException ex)
        {
            // #98: reject adds that would create a cycle in the unit
            // containment graph. 409 Conflict matches the ProblemDetails
            // shape established by #192 for rejected state changes.
            return Results.Problem(
                title: "Cyclic unit membership",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["parentUnit"] = $"{ex.ParentUnit.Scheme}://{ex.ParentUnit.Path}",
                    ["candidateMember"] = $"{ex.CandidateMember.Scheme}://{ex.CandidateMember.Path}",
                    ["cyclePath"] = ex.CyclePath
                        .Select(a => $"{a.Scheme}://{a.Path}")
                        .ToArray(),
                });
        }

        // Previous behaviour returned `{ Status = "Member added" }`; the
        // string carried no new information beyond the HTTP status, and the
        // anonymous shape kept the endpoint out of the OpenAPI contract.
        // 204 says the same thing with a standard signal (#172).
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMemberAsync(
        string id,
        string memberId,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // The caller's memberId is an opaque path; without a scheme it is
        // ambiguous. Historically the endpoint sent a Domain message shaped
        // { Action = "RemoveMember", MemberId } that no handler ever read,
        // so no member was removed. Now we try both "agent://" and "unit://"
        // spellings against the persisted member list so existing callers
        // continue to work regardless of member scheme. Remove is idempotent
        // — no cycle check is required.
        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(IUnitActor));

        await unitProxy.RemoveMemberAsync(new Address("agent", memberId), cancellationToken);
        await unitProxy.RemoveMemberAsync(new Address("unit", memberId), cancellationToken);

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
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        if (!Enum.TryParse<PermissionLevel>(request.Permission, ignoreCase: true, out var permissionLevel))
        {
            return Results.Problem(detail: $"Invalid permission level: '{request.Permission}'", statusCode: StatusCodes.Status400BadRequest);
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

        return Results.Ok(new SetHumanPermissionResponse(humanId, permissionLevel));
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
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(IUnitActor));

        var permissions = await unitProxy.GetHumanPermissionsAsync(cancellationToken);

        return Results.Ok(permissions);
    }

    /// <summary>
    /// Invokes <see cref="IConnectorType.OnUnitStartingAsync"/> on the
    /// connector type the unit is currently bound to, if any. The unit is
    /// "bound" when its <see cref="IUnitActor.GetConnectorBindingAsync"/>
    /// returns a <see cref="UnitConnectorBinding"/> whose type id matches a
    /// registered <see cref="IConnectorType"/>.
    /// </summary>
    private static async Task DispatchConnectorStartAsync(
        string unitId,
        IUnitActor proxy,
        IEnumerable<IConnectorType> connectorTypes,
        ILogger logger,
        CancellationToken ct)
    {
        var binding = await proxy.GetConnectorBindingAsync(ct);
        if (binding is null)
        {
            return;
        }

        var connector = connectorTypes.FirstOrDefault(c => c.TypeId == binding.TypeId);
        if (connector is null)
        {
            logger.LogWarning(
                "Unit {UnitId} is bound to connector type {TypeId} which is not registered; skipping start hook.",
                unitId, binding.TypeId);
            return;
        }

        try
        {
            await connector.OnUnitStartingAsync(unitId, ct);
        }
        catch (Exception ex)
        {
            // Any connector start failure is non-fatal — the unit
            // transitions to Running regardless so the container stays up.
            logger.LogError(ex,
                "Connector {Slug} start hook threw for unit {UnitId}; continuing unit start.",
                connector.Slug, unitId);
        }
    }

    /// <summary>
    /// Mirrors <see cref="DispatchConnectorStartAsync"/> for the stop path.
    /// </summary>
    private static async Task DispatchConnectorStopAsync(
        string unitId,
        IUnitActor proxy,
        IEnumerable<IConnectorType> connectorTypes,
        ILogger logger,
        CancellationToken ct)
    {
        var binding = await proxy.GetConnectorBindingAsync(ct);
        if (binding is null)
        {
            return;
        }

        var connector = connectorTypes.FirstOrDefault(c => c.TypeId == binding.TypeId);
        if (connector is null)
        {
            return;
        }

        try
        {
            await connector.OnUnitStoppingAsync(unitId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Connector {Slug} stop hook threw for unit {UnitId}; continuing unit stop.",
                connector.Slug, unitId);
        }
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

    private static async Task<IResult> ListUnitAgentsAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");
        var unitAddress = new Address("unit", id);
        var unitEntry = await directoryService.ResolveAsync(unitAddress, cancellationToken);

        if (unitEntry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(unitEntry.ActorId), nameof(IUnitActor));
        var members = await unitProxy.GetMembersAsync(cancellationToken);

        // Filter to agent members; sub-unit members are out of scope here
        // and surface through a (future) /sub-units sub-route.
        var agentMembers = members
            .Where(m => string.Equals(m.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Resolve and enrich in parallel. N+1 is fine here — units typically
        // hold single-digit numbers of agents, and the actor metadata read is
        // a single state lookup. ParentUnit on each response is derived
        // from the membership table, not read from the legacy cached state.
        var enrichmentTasks = agentMembers.Select(async member =>
        {
            var entry = await directoryService.ResolveAsync(member, cancellationToken);
            if (entry is null)
            {
                // Member address no longer in the directory — skip rather
                // than synthesising a half-populated response.
                logger.LogWarning(
                    "Unit {UnitId} lists member {Member} but the directory has no entry for it.",
                    id, member);
                return null;
            }
            var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
                new ActorId(entry.ActorId), nameof(IAgentActor));
            var metadata = await AgentEndpoints.GetDerivedAgentMetadataAsync(
                proxy, membershipRepository, member.Path, cancellationToken);
            return AgentEndpoints.ToAgentResponse(entry, metadata);
        });

        var responses = (await Task.WhenAll(enrichmentTasks))
            .Where(r => r is not null)
            .Cast<AgentResponse>()
            .ToList();

        return Results.Ok(responses);
    }

    private static async Task<IResult> AssignUnitAgentAsync(
        string id,
        string agentId,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");

        if (string.IsNullOrWhiteSpace(agentId))
        {
            return Results.Problem(detail: "agentId is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var unitAddress = new Address("unit", id);
        var unitEntry = await directoryService.ResolveAsync(unitAddress, cancellationToken);
        if (unitEntry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var agentAddress = new Address("agent", agentId);
        var agentEntry = await directoryService.ResolveAsync(agentAddress, cancellationToken);
        if (agentEntry is null)
        {
            return Results.Problem(detail: $"Agent '{agentId}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // C2b-1: M:N membership model (see #160). An agent may be a member
        // of multiple units. No 1:N conflict check — the old guard is gone
        // and operators may freely add the same agent to several units.
        // Existing membership rows are preserved (idempotent re-assign).
        var existing = await membershipRepository.GetAsync(id, agentId, cancellationToken);
        var membership = existing is null
            ? new UnitMembership(UnitId: id, AgentAddress: agentId, Enabled: true)
            : existing with { UnitId = id, AgentAddress = agentId };

        await membershipRepository.UpsertAsync(membership, cancellationToken);

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(unitEntry.ActorId), nameof(IUnitActor));
        await unitProxy.AddMemberAsync(agentAddress, cancellationToken);

        // Also sync the legacy cached pointer on the agent actor so any
        // reader still relying on it sees a consistent value. The
        // authoritative source is the membership table.
        var agentProxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(agentEntry.ActorId), nameof(IAgentActor));
        await agentProxy.SetMetadataAsync(
            new AgentMetadata(ParentUnit: id),
            cancellationToken);

        logger.LogInformation(
            "Agent {AgentId} assigned to unit {UnitId}.", agentId, id);

        var refreshed = await AgentEndpoints.GetDerivedAgentMetadataAsync(
            agentProxy, membershipRepository, agentId, cancellationToken);
        return Results.Ok(AgentEndpoints.ToAgentResponse(agentEntry, refreshed));
    }

    private static async Task<IResult> UnassignUnitAgentAsync(
        string id,
        string agentId,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");

        var unitAddress = new Address("unit", id);
        var unitEntry = await directoryService.ResolveAsync(unitAddress, cancellationToken);
        if (unitEntry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var agentAddress = new Address("agent", agentId);
        var agentEntry = await directoryService.ResolveAsync(agentAddress, cancellationToken);
        if (agentEntry is null)
        {
            return Results.Problem(detail: $"Agent '{agentId}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // Delete the membership row. Other units the agent still belongs to
        // are unaffected — this is the point of M:N.
        await membershipRepository.DeleteAsync(id, agentId, cancellationToken);

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(unitEntry.ActorId), nameof(IUnitActor));
        await unitProxy.RemoveMemberAsync(agentAddress, cancellationToken);

        // Refresh the cached pointer on the agent actor. If any memberships
        // remain, the derivation rule (first by CreatedAt) picks the new
        // "primary" unit; if this was the last membership, clear the pointer.
        var remaining = await membershipRepository.ListByAgentAsync(agentId, cancellationToken);
        var agentProxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(agentEntry.ActorId), nameof(IAgentActor));
        if (remaining.Count == 0)
        {
            await agentProxy.ClearParentUnitAsync(cancellationToken);
        }
        else
        {
            await agentProxy.SetMetadataAsync(
                new AgentMetadata(ParentUnit: remaining[0].UnitId),
                cancellationToken);
        }

        logger.LogInformation(
            "Agent {AgentId} unassigned from unit {UnitId}.", agentId, id);

        return Results.NoContent();
    }
}