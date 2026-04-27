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
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        var group = app.MapGroup("/api/v1/tenant/units")
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

        group.MapGet("/{id}/readiness", GetUnitReadinessAsync)
            .WithName("GetUnitReadiness")
            .WithSummary("Check whether a unit is ready to leave Draft and be started")
            .WithDescription("Returns readiness status and a list of missing requirements. Useful for the UI to enable/disable the Start button.")
            .Produces<UnitReadinessResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

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

        group.MapPost("/{id}/revalidate", RevalidateUnitAsync)
            .WithName("RevalidateUnit")
            .WithSummary("Re-run backend validation for a unit in Error or Stopped state")
            .WithDescription("Transitions the unit into Validating and kicks off a new UnitValidationWorkflow run. The handler returns immediately — progress is observable via SSE ValidationProgress events and the terminal state is written back by the workflow.")
            .Produces<UnitResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id}/members", ListUnitMembersAsync)
            .WithName("ListUnitMembers")
            .WithSummary("List all members of a unit (agents and sub-units)")
            .WithDescription("Returns the full member list from the unit actor, including both agent-scheme and unit-scheme members.")
            .Produces<AddressDto[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

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
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Generic (non-polymorphic) pointer endpoints — the typed per-unit
        // config lives on the connector package's own surface under
        // /api/v1/connectors/{slug}/units/{unitId}/config.
        group.MapUnitConnectorPointerEndpoints();

        // Permission gates on the /humans sub-routes run *inside* the
        // handler (via UnitPermissionCheck) rather than through a
        // declarative RequireAuthorization(PermissionPolicies.Unit*) on
        // the route. The declarative path evaluated authorisation before
        // the handler and failed closed on an unknown unit — surfacing 403
        // instead of 404 and leaking existence (#1029). Authentication
        // still runs ahead of the handler via the group-level
        // RequireAuthorization() call in Program.cs.
        group.MapPatch("/{id}/humans/{humanId}/permissions", SetHumanPermissionAsync)
            .WithName("SetHumanPermission")
            .WithSummary("Set permission level for a human within a unit")
            .Produces<SetHumanPermissionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/humans", GetHumanPermissionsAsync)
            .WithName("GetHumanPermissions")
            .WithSummary("Get all human permissions for a unit")
            .Produces<IReadOnlyList<UnitPermissionEntry>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // DELETE pairs with PATCH above so `spring unit humans remove` has a
        // dedicated call — the PATCH endpoint has no "unset" shape. Idempotent:
        // removing an entry that does not exist still returns 204 so the CLI
        // does not need to branch on "never set" vs "already removed".
        // Owner-gated to match the PATCH authorisation policy.
        group.MapDelete("/{id}/humans/{humanId}/permissions", RemoveHumanPermissionAsync)
            .WithName("RemoveHumanPermission")
            .WithSummary("Remove a human's permission entry from a unit")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
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
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

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
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IServiceScopeFactory scopeFactory,
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
        var validationTracking = await TryGetValidationTrackingAsync(
            scopeFactory, entry.ActorId, logger, id, cancellationToken);

        // #339: Read the unit's status-query payload (status + member count)
        // by calling the actor proxy directly, bypassing the message router.
        // The router's permission gate is for external human-originated
        // dispatch — a platform-internal read path must not be refused just
        // because the hardcoded synthetic From lacks Viewer permission on
        // units created post-#328. The payload shape must stay byte-
        // compatible with UnitActor.HandleStatusQueryAsync so clients that
        // parse the Details envelope keep working.
        var details = await TryGetUnitStatusPayloadAsync(
            actorProxyFactory, entry.ActorId, logger, id, cancellationToken);

        var unitResponse = ToUnitResponse(entry, status, metadata, validationTracking);
        return Results.Ok(new UnitDetailResponse(unitResponse, details));
    }

    /// <summary>
    /// Reads the unit's status-query payload (<c>{Status, MemberCount}</c>)
    /// through the actor proxy. Returns <c>null</c> when the actor cannot be
    /// reached — mirroring the pre-#339 behaviour that surfaced a null
    /// <c>Details</c> field on transient failure — but no longer collapses
    /// to null just because the router's permission gate refuses a
    /// platform-internal dispatch.
    /// </summary>
    private static async Task<JsonElement?> TryGetUnitStatusPayloadAsync(
        IActorProxyFactory actorProxyFactory,
        string actorId,
        ILogger logger,
        string unitId,
        CancellationToken cancellationToken)
    {
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(actorId), nameof(UnitActor));

            var status = await proxy.GetStatusAsync(cancellationToken);
            var members = await proxy.GetMembersAsync(cancellationToken);

            // #339: surface the full members list alongside the prior
            // {Status, MemberCount} shape. The web UI and e2e/12-nested-
            // units.sh both consult the members list to verify containment;
            // the old HandleStatusQueryAsync payload only exposed a count,
            // which is why the scenario aborted once the permission gate
            // started denying the synthetic-From dispatch.
            return JsonSerializer.SerializeToElement(new
            {
                Status = status.ToString(),
                MemberCount = members.Length,
                Members = members.Select(m => new { Scheme = m.Scheme, Path = m.Path }).ToArray(),
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to read status-query payload for unit {UnitId}; returning null details.",
                unitId);
            return null;
        }
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
                new ActorId(actorId), nameof(UnitActor));
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
                new ActorId(actorId), nameof(UnitActor));
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
        catch (InvalidUnitParentRequestException ex)
        {
            // Review feedback on #744: neither / both of parentUnitIds +
            // isTopLevel is a client error, distinct from the "unit name
            // collision" 400 above.
            return Results.Problem(
                title: "Unit parent required",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (UnknownParentUnitException ex)
        {
            return Results.Problem(
                title: "Unknown parent unit",
                detail: ex.Message,
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (UnitCreationBindingException ex)
        {
            return ProblemFromBindingFailure(ex);
        }
        catch (DuplicateUnitNameException ex)
        {
            return Results.Problem(title: "Duplicate unit name", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
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

        var overrides = new UnitCreationOverrides(request.DisplayName, request.Color, request.Model,
            Tool: request.Tool, Provider: request.Provider, Hosting: request.Hosting,
            ParentUnitIds: request.ParentUnitIds, IsTopLevel: request.IsTopLevel);
        try
        {
            var result = await creationService.CreateFromManifestAsync(
                manifest, overrides, cancellationToken, request.Connector);

            return Results.Created(
                $"/api/v1/units/{result.Unit.Name}",
                new UnitCreationResponse(result.Unit, result.Warnings, result.MembersAdded));
        }
        catch (InvalidUnitParentRequestException ex)
        {
            return Results.Problem(
                title: "Unit parent required",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (UnknownParentUnitException ex)
        {
            return Results.Problem(
                title: "Unknown parent unit",
                detail: ex.Message,
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (UnitCreationBindingException ex)
        {
            return ProblemFromBindingFailure(ex);
        }
        catch (DuplicateUnitNameException ex)
        {
            return Results.Problem(title: "Duplicate unit name", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
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

        // #325: forward the optional unit-name override so the caller can
        // instantiate the same template more than once without colliding
        // on the unique-name constraint.
        var overrides = new UnitCreationOverrides(
            request.DisplayName,
            request.Color,
            request.Model,
            request.UnitName,
            request.Tool,
            request.Provider,
            request.Hosting,
            request.ParentUnitIds,
            request.IsTopLevel);
        try
        {
            var result = await creationService.CreateFromManifestAsync(
                manifest, overrides, cancellationToken, request.Connector);

            return Results.Created(
                $"/api/v1/units/{result.Unit.Name}",
                new UnitCreationResponse(result.Unit, result.Warnings, result.MembersAdded));
        }
        catch (InvalidUnitParentRequestException ex)
        {
            return Results.Problem(
                title: "Unit parent required",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (UnknownParentUnitException ex)
        {
            return Results.Problem(
                title: "Unknown parent unit",
                detail: ex.Message,
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (UnitCreationBindingException ex)
        {
            return ProblemFromBindingFailure(ex);
        }
        catch (DuplicateUnitNameException ex)
        {
            return Results.Problem(title: "Duplicate unit name", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
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
            new ActorId(entry.ActorId), nameof(UnitActor));

        var metadata = new UnitMetadata(
            DisplayName: request.DisplayName,
            Description: request.Description,
            Model: request.Model,
            Color: request.Color,
            Tool: request.Tool,
            Provider: request.Provider,
            Hosting: request.Hosting);

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
            new ActorId(actorId), nameof(UnitActor));

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

    private static async Task<IResult> GetUnitReadinessAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var address = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(UnitActor));

        var readiness = await proxy.CheckReadinessAsync(cancellationToken);
        return Results.Ok(new UnitReadinessResponse(readiness.IsReady, readiness.MissingRequirements));
    }

    private static async Task<IResult> StartUnitAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
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
            new ActorId(entry.ActorId), nameof(UnitActor));

        var startingTransition = await proxy.TransitionAsync(UnitStatus.Starting, cancellationToken);
        if (!startingTransition.Success)
        {
            return Results.Conflict(new
            {
                Error = startingTransition.RejectionReason,
                CurrentStatus = startingTransition.CurrentStatus
            });
        }

        // Dispatch connector start-hooks so each connector can provision
        // any external-system resources its binding needs (e.g. GitHub
        // webhooks). Each connector is responsible for catching its own
        // failures — we never let a misbehaving connector fail a unit start.
        await DispatchConnectorStartAsync(
            id, proxy, connectorTypes, logger, cancellationToken);

        // Transition straight to Running. Agent-container lifecycle is
        // managed by the A2A dispatcher (#346/#349), not by this endpoint.
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
            new ActorId(entry.ActorId), nameof(UnitActor));

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

        // Transition straight to Stopped. Agent-container lifecycle is
        // managed by the A2A dispatcher (#346/#349), not by this endpoint.
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

    /// <summary>
    /// Handler for <c>POST /api/v1/units/{id}/revalidate</c>. Allowed only
    /// from <see cref="UnitStatus.Error"/> or <see cref="UnitStatus.Stopped"/>
    /// per the design approved on #942 — any other status returns 409 with a
    /// structured <c>currentStatus</c> detail so the client can surface
    /// guidance. The handler returns 202 immediately; the workflow's terminal
    /// activity drives the follow-up <see cref="UnitStatus.Validating"/> →
    /// <see cref="UnitStatus.Stopped"/> or <see cref="UnitStatus.Error"/>
    /// transition via <see cref="IUnitActor.CompleteValidationAsync"/>.
    /// </summary>
    private static async Task<IResult> RevalidateUnitAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IServiceScopeFactory scopeFactory,
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
        if (status != UnitStatus.Error && status != UnitStatus.Stopped)
        {
            return Results.Problem(
                title: "Invalid state",
                detail: $"Unit '{id}' is {status}; revalidation is only allowed from Error or Stopped.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "InvalidState",
                    ["currentStatus"] = status.ToString(),
                });
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(UnitActor));

        var transition = await proxy.TransitionAsync(UnitStatus.Validating, cancellationToken);
        if (!transition.Success)
        {
            return Results.Problem(
                title: "Invalid state",
                detail: transition.RejectionReason ?? "Unit could not enter Validating.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "InvalidState",
                    ["currentStatus"] = transition.CurrentStatus.ToString(),
                });
        }

        // The entity write (LastValidationRunId + cleared
        // LastValidationErrorJson) happens inside the actor's transition
        // path. Read metadata + tracking back to echo a consistent DTO on
        // the 202 response.
        var metadata = await TryGetUnitMetadataAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);
        var validationTracking = await TryGetValidationTrackingAsync(
            scopeFactory, entry.ActorId, logger, id, cancellationToken);

        return Results.Accepted(
            $"/api/v1/units/{id}",
            ToUnitResponse(entry, transition.CurrentStatus, metadata, validationTracking));
    }

    private static async Task<IResult> ListUnitMembersAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(UnitActor));
        var members = await proxy.GetMembersAsync(cancellationToken);

        var result = members
            .Select(m => new AddressDto(m.Scheme, m.Path))
            .ToArray();

        return Results.Ok(result);
    }

    private static async Task<IResult> AddMemberAsync(
        string id,
        AddMemberRequest request,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IExpertiseAggregator expertiseAggregator,
        IUnitMembershipTenantGuard tenantGuard,
        CancellationToken cancellationToken)
    {
        var unitAddress = new Address("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var memberAddress = new Address(request.MemberAddress.Scheme, request.MemberAddress.Path);

        // #745: enforce same-tenant before any actor-state write. Cross-
        // tenant members would let a message dispatched to unit A reach an
        // agent or sub-unit in tenant B.
        try
        {
            await tenantGuard.EnsureSameTenantAsync(unitAddress, memberAddress, cancellationToken);
        }
        catch (CrossTenantMembershipException ex)
        {
            return Results.Problem(
                title: "Member not found in this tenant",
                detail: ex.Message,
                statusCode: StatusCodes.Status404NotFound);
        }

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(UnitActor));

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

        // Membership change reshapes the unit's effective expertise (#412).
        await expertiseAggregator.InvalidateAsync(unitAddress, cancellationToken);

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
        IExpertiseAggregator expertiseAggregator,
        IUnitParentInvariantGuard parentGuard,
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
            new ActorId(entry.ActorId), nameof(UnitActor));

        // Review feedback on #744: the unit variant of memberId must carry
        // the same "no un-parenting" invariant the agent-removal path
        // already enforces. Ask the guard before the actor-state write so
        // we reject the removal with a 409 instead of leaving the child
        // unit parentless and non-top-level. Top-level children and
        // non-registered children pass through (see
        // UnitParentInvariantGuard for the exact branches).
        try
        {
            await parentGuard.EnsureParentRemainsAsync(
                unitAddress,
                new Address("unit", memberId),
                cancellationToken);
        }
        catch (UnitParentRequiredException ex)
        {
            return Results.Problem(
                title: "Unit parent required",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["unitAddress"] = ex.UnitAddress,
                    ["parentUnitId"] = ex.ParentUnitId,
                });
        }

        await unitProxy.RemoveMemberAsync(new Address("agent", memberId), cancellationToken);
        await unitProxy.RemoveMemberAsync(new Address("unit", memberId), cancellationToken);

        await expertiseAggregator.InvalidateAsync(unitAddress, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> SetHumanPermissionAsync(
        string id,
        string humanId,
        SetHumanPermissionRequest request,
        HttpContext httpContext,
        IDirectoryService directoryService,
        IPermissionService permissionService,
        IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var auth = await UnitPermissionCheck.AuthorizeAsync(
            id,
            PermissionLevel.Owner,
            directoryService,
            permissionService,
            httpContext,
            cancellationToken);
        if (!auth.Authorized)
        {
            return auth.ToErrorResult(id);
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
            new ActorId(auth.Entry!.ActorId), nameof(UnitActor));

        await unitProxy.SetHumanPermissionAsync(humanId, permissionEntry, cancellationToken);

        // Also update the human actor's unit-scoped permission map.
        var humanProxy = actorProxyFactory.CreateActorProxy<IHumanActor>(
            new ActorId(humanId), nameof(HumanActor));

        await humanProxy.SetPermissionForUnitAsync(id, permissionLevel, cancellationToken);

        return Results.Ok(new SetHumanPermissionResponse(humanId, permissionLevel));
    }

    private static async Task<IResult> GetHumanPermissionsAsync(
        string id,
        HttpContext httpContext,
        IDirectoryService directoryService,
        IPermissionService permissionService,
        IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var auth = await UnitPermissionCheck.AuthorizeAsync(
            id,
            PermissionLevel.Viewer,
            directoryService,
            permissionService,
            httpContext,
            cancellationToken);
        if (!auth.Authorized)
        {
            return auth.ToErrorResult(id);
        }

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(auth.Entry!.ActorId), nameof(UnitActor));

        var permissions = await unitProxy.GetHumanPermissionsAsync(cancellationToken);

        return Results.Ok(permissions);
    }

    /// <summary>
    /// Handler for <c>DELETE /api/v1/units/{id}/humans/{humanId}/permissions</c>.
    /// Pairs with <see cref="SetHumanPermissionAsync"/> so
    /// <c>spring unit humans remove</c> has a dedicated endpoint. Returns 204
    /// whether or not the human had an entry — the desired end state is "no
    /// entry for this human on this unit" regardless of the prior state, so
    /// the CLI stays a simple one-shot without retry branching.
    /// </summary>
    private static async Task<IResult> RemoveHumanPermissionAsync(
        string id,
        string humanId,
        HttpContext httpContext,
        IDirectoryService directoryService,
        IPermissionService permissionService,
        IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var auth = await UnitPermissionCheck.AuthorizeAsync(
            id,
            PermissionLevel.Owner,
            directoryService,
            permissionService,
            httpContext,
            cancellationToken);
        if (!auth.Authorized)
        {
            return auth.ToErrorResult(id);
        }

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(auth.Entry!.ActorId), nameof(UnitActor));

        await unitProxy.RemoveHumanPermissionAsync(humanId, cancellationToken);

        // Keep the human actor's unit-scoped map in sync so readers on the
        // human side see the removal immediately — matches what
        // SetHumanPermissionAsync does on the write path.
        var humanProxy = actorProxyFactory.CreateActorProxy<IHumanActor>(
            new ActorId(humanId), nameof(HumanActor));

        await humanProxy.RemovePermissionForUnitAsync(id, cancellationToken);

        return Results.NoContent();
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
        UnitMetadata? metadata = null,
        UnitValidationTracking? validationTracking = null) =>
        new(
            entry.ActorId,
            entry.Address.Path,
            entry.DisplayName,
            entry.Description,
            entry.RegisteredAt,
            status,
            metadata?.Model,
            metadata?.Color,
            metadata?.Tool,
            metadata?.Provider,
            metadata?.Hosting,
            validationTracking?.LastValidationError,
            validationTracking?.LastValidationRunId);

    /// <summary>
    /// View of the per-unit validation-tracking columns projected into the
    /// GET DTO. Parsed once per read via
    /// <see cref="TryGetValidationTrackingAsync"/> so the endpoint does not
    /// repeat the JSON parse in multiple code paths.
    /// </summary>
    private sealed record UnitValidationTracking(
        UnitValidationError? LastValidationError,
        string? LastValidationRunId);

    /// <summary>
    /// Reads the unit's <c>LastValidationErrorJson</c> / <c>LastValidationRunId</c>
    /// columns via a scoped <see cref="SpringDbContext"/> and returns a
    /// parsed view suitable for projection into <see cref="UnitResponse"/>.
    /// Returns <c>null</c> when the row is missing or the context is not
    /// registered (design-time / doc-gen path) so the DTO's null values
    /// surface naturally.
    /// </summary>
    private static async Task<UnitValidationTracking?> TryGetValidationTrackingAsync(
        IServiceScopeFactory scopeFactory,
        string actorId,
        ILogger logger,
        string unitId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var row = await db.UnitDefinitions
                .AsNoTracking()
                .Where(u => u.ActorId == actorId && u.DeletedAt == null)
                .Select(u => new
                {
                    u.LastValidationErrorJson,
                    u.LastValidationRunId,
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (row is null)
            {
                return null;
            }

            UnitValidationError? error = null;
            if (!string.IsNullOrWhiteSpace(row.LastValidationErrorJson))
            {
                try
                {
                    error = JsonSerializer.Deserialize<UnitValidationError>(
                        row.LastValidationErrorJson);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Unit {UnitId}: failed to parse LastValidationErrorJson; omitting from response.",
                        unitId);
                }
            }

            return new UnitValidationTracking(error, row.LastValidationRunId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Unit {UnitId}: failed to read validation tracking columns; omitting from response.",
                unitId);
            return null;
        }
    }

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
            new ActorId(unitEntry.ActorId), nameof(UnitActor));
        var members = await unitProxy.GetMembersAsync(cancellationToken);

        // Filter to agent members; sub-unit members are out of scope here
        // and surface through a (future) /sub-units sub-route.
        var agentMembers = members
            .Where(m => string.Equals(m.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Resolve and enrich sequentially. Units typically hold single-digit
        // numbers of agents, so the N+1 cost is negligible. A previous
        // implementation ran the enrichment tasks concurrently via
        // Task.WhenAll, but that funneled parallel reads through the same
        // scoped SpringDbContext (via GetDerivedAgentMetadataAsync →
        // IUnitMembershipRepository), which is not thread-safe and surfaced
        // as "A second operation was started on this context instance" ->
        // HTTP 500 for the Skills settings tab (issue #600). ParentUnit on
        // each response is derived from the membership table, not read from
        // the legacy cached state.
        var responses = new List<AgentResponse>(agentMembers.Count);
        foreach (var member in agentMembers)
        {
            var entry = await directoryService.ResolveAsync(member, cancellationToken);
            if (entry is null)
            {
                // Member address no longer in the directory — skip rather
                // than synthesising a half-populated response.
                logger.LogWarning(
                    "Unit {UnitId} lists member {Member} but the directory has no entry for it.",
                    id, member);
                continue;
            }
            var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
                new ActorId(entry.ActorId), nameof(AgentActor));
            var metadata = await AgentEndpoints.GetDerivedAgentMetadataAsync(
                proxy, membershipRepository, member.Path, cancellationToken);
            responses.Add(AgentEndpoints.ToAgentResponse(entry, metadata));
        }

        return Results.Ok(responses);
    }

    private static async Task<IResult> AssignUnitAgentAsync(
        string id,
        string agentId,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromServices] IExpertiseAggregator expertiseAggregator,
        [FromServices] IUnitMembershipTenantGuard tenantGuard,
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

        // #745: enforce same-tenant before the membership write. The
        // directory still services cross-tenant Resolve* calls out of a
        // shared in-memory cache (the DirectoryService cache isn't tenant-
        // aware yet), so the guard is the authoritative seam for this
        // invariant.
        try
        {
            await tenantGuard.EnsureSameTenantAsync(unitAddress, agentAddress, cancellationToken);
        }
        catch (CrossTenantMembershipException ex)
        {
            return Results.Problem(
                title: "Agent not found in this tenant",
                detail: ex.Message,
                statusCode: StatusCodes.Status404NotFound);
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
            new ActorId(unitEntry.ActorId), nameof(UnitActor));
        await unitProxy.AddMemberAsync(agentAddress, cancellationToken);

        // Also sync the legacy cached pointer on the agent actor so any
        // reader still relying on it sees a consistent value. The
        // authoritative source is the membership table.
        var agentProxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(agentEntry.ActorId), nameof(AgentActor));
        await agentProxy.SetMetadataAsync(
            new AgentMetadata(ParentUnit: id),
            cancellationToken);

        // Membership change reshapes the unit's effective expertise and,
        // transitively, every ancestor unit's aggregated view (#412).
        await expertiseAggregator.InvalidateAsync(unitAddress, cancellationToken);

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
        [FromServices] IExpertiseAggregator expertiseAggregator,
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
        // are unaffected — this is the point of M:N. Per #744 the repo
        // rejects removal when this is the agent's last membership; we
        // surface that as 409 so the caller either assigns the agent to
        // another unit first or deletes the agent via DELETE /agents/{id}.
        try
        {
            await membershipRepository.DeleteAsync(id, agentId, cancellationToken);
        }
        catch (AgentMembershipRequiredException ex)
        {
            return Results.Problem(
                title: "Agent must belong to at least one unit",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["agentAddress"] = ex.AgentAddress,
                    ["unitId"] = ex.UnitId,
                });
        }

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(unitEntry.ActorId), nameof(UnitActor));
        await unitProxy.RemoveMemberAsync(agentAddress, cancellationToken);

        // Refresh the cached pointer on the agent actor. If any memberships
        // remain, the derivation rule (first by CreatedAt) picks the new
        // "primary" unit; if this was the last membership, clear the pointer.
        var remaining = await membershipRepository.ListByAgentAsync(agentId, cancellationToken);
        var agentProxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(agentEntry.ActorId), nameof(AgentActor));
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

        // Invalidate the aggregator cache up the chain: removing the agent
        // changes this unit's effective expertise and every ancestor's.
        await expertiseAggregator.InvalidateAsync(unitAddress, cancellationToken);

        logger.LogInformation(
            "Agent {AgentId} unassigned from unit {UnitId}.", agentId, id);

        return Results.NoContent();
    }
}