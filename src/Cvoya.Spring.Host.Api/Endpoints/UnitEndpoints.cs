// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Units;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;

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

        group.MapPatch("/{id}", UpdateUnitAsync)
            .WithName("UpdateUnit")
            .WithSummary("Update mutable unit metadata (displayName, description, model, color, hosting, instructions)")
            // #2293: the handler reads the body via HttpContext so it can
            // distinguish absent-vs-explicit-null on the `instructions`
            // tri-state, so we declare the contract surface via .Accepts.
            .Accepts<UpdateUnitRequest>("application/json")
            .Produces<UnitResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
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

        group.MapGet("/{id}/deployment", GetUnitDeploymentAsync)
            .WithName("GetUnitDeployment")
            .WithSummary("Get the deployment / lifecycle status for a unit")
            .WithDescription("Returns whether the unit is running and its current status label. Mirrors the agent deployment surface so the portal's Deployment tab renders start/stop controls for units and agents identically.")
            .Produces<UnitDeploymentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // #2360: equipped skill bundles on a unit. The pre-#2360 `/skills`
        // surface returned a flat string[] of tool names (legacy MCP-tool
        // grant shape from the Tools wave); it was superseded by the
        // proper Tools surface under Config → Tools. The new shape under
        // this route is operator-equip of skill bundles, which feeds
        // Layer 2 of the assembled prompt for the unit and is inherited
        // by member-agent dispatches as part of that layer.
        group.MapGet("/{id}/skills", GetEquippedSkillsAsync)
            .WithName("GetUnitSkills")
            .WithSummary("List the skill bundles equipped on a unit")
            .WithDescription("Returns the resolved bundles in declaration order. Each entry carries the package + skill coordinates, a prompt-body summary, and the bundle's required-tool list.")
            .Produces<EquippedSkillsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/skills", EquipUnitSkillAsync)
            .WithName("EquipUnitSkill")
            .WithSummary("Equip a skill bundle on a unit")
            .WithDescription("Idempotent on (packageName, skillName). The store re-resolves the bundle so the persisted record carries the freshest prompt + required-tools snapshot. Returns the new effective list in declaration order.")
            .Produces<EquippedSkillsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}/skills/{packageName}/{skillName}", UnequipUnitSkillAsync)
            .WithName("UnequipUnitSkill")
            .WithSummary("Unequip a skill bundle from a unit")
            .WithDescription("No-op when the bundle is not currently equipped. Returns the new effective list.")
            .Produces<EquippedSkillsResponse>(StatusCodes.Status200OK)
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
            .WithDescription("Transitions the unit into Validating and kicks off a new ArtefactValidationWorkflow run. The handler returns immediately — progress is observable via SSE ValidationProgress events and the terminal state is written back by the workflow.")
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
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

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
            .ProducesProblem(StatusCodes.Status404NotFound)
            // ADR-0039 §6 / B3: when assigning an agent to a unit expands its
            // parent set into a state where an inherited execution-config
            // field diverges across parents, the platform rejects with 422
            // and a structured `MultiParentInheritanceConflict` body so the
            // operator can either trim the parent set or set the field
            // explicitly on the agent.
            .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{id}/agents/{agentId}", UnassignUnitAgentAsync)
            .WithName("UnassignUnitAgent")
            .WithSummary("Unassign an agent from this unit. Deletes the membership row and removes the agent from the unit's members list; other memberships the agent holds are unaffected.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            // ADR-0039 §6 / B4: unassigning can leave the agent inheriting
            // from a different parent set. If the remaining parents disagree
            // on an inherited execution-config field, reject with the same
            // structured conflict body as assignment.
            .Produces(StatusCodes.Status422UnprocessableEntity);

        // Portal runtime-status indicator (#2100). Same shape as the agent
        // variant on AgentEndpoints — units are agents per ADR-0017 and
        // the portal renders the same status chip next to every name.
        // Today the unit actor reports zero in-flight / queued because it
        // does not yet maintain per-thread channels (every domain
        // message goes straight through `_runtimeInvocationPath`); the
        // endpoint still reaches the actor for forward-compatibility and
        // combines the result with the persistent-registry health probe.
        group.MapGet("/{id}/runtime-status", GetUnitRuntimeStatusAsync)
            .WithName("GetUnitRuntimeStatus")
            .WithSummary("Get the unit's runtime-status indicator (idle / busy / queued / unavailable)")
            .Produces<AgentRuntimeStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    /// <summary>
    /// Returns the runtime-status indicator for a unit (#2100). Mirrors
    /// <c>AgentEndpoints.GetAgentRuntimeStatusAsync</c>: deployment-health
    /// gate first; on healthy / not-deployed, ask the unit actor for its
    /// per-thread channel snapshot (populated by the unit's dispatch
    /// tracker as of #2491).
    /// </summary>
    private static async Task<IResult> GetUnitRuntimeStatusAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IExecutionHostGateway executionGateway,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("unit", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);

        // Unlike agents, units don't carry an `execution.hosting` slot —
        // they are always container-backed when running. We treat
        // "deployment running but unhealthy" as `unavailable`;
        // "not deployed" is the "not yet started / never deployed"
        // case which we report as `idle` rather than `unavailable` so
        // the chip doesn't scream on every Draft / Stopped unit. Operators
        // who want lifecycle-state visibility have the `LifecycleStatus` field
        // (Draft / Validating / Stopped / Error) on the unit detail
        // endpoint. ADR-0052 / #2618: deployment health is read from the
        // execution host over the gateway; fail-open (treat as healthy) so a
        // transient worker hiccup doesn't blank the chip.
        PersistentAgentDeploymentState? deploymentState = null;
        try
        {
            deploymentState = await executionGateway.GetDeploymentAsync(actorId, cancellationToken);
        }
        catch (SpringException)
        {
            // Fail-open per the comment above.
        }

        if (deploymentState is { Running: true }
            && !string.Equals(deploymentState.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new AgentRuntimeStatusResponse(
                Status: "unavailable",
                LastUpdated: DateTimeOffset.UtcNow,
                InFlightThreadCount: 0,
                QueuedMessageCount: 0));
        }

        Cvoya.Spring.Core.Agents.AgentRuntimeStatusReport? report = null;
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(actorId), nameof(UnitActor));
            report = await proxy.GetRuntimeStatusAsync(cancellationToken);
        }
        catch (Exception)
        {
            // swallow — best-effort indicator, idle is safer than 5xx
        }

        report ??= new Cvoya.Spring.Core.Agents.AgentRuntimeStatusReport(
            InFlightThreadCount: 0,
            QueuedMessageCount: 0,
            ChannelCount: 0,
            ObservedAt: DateTimeOffset.UtcNow);

        return Results.Ok(AgentEndpoints.ProjectRuntimeStatus(report));
    }

    private static async Task<IResult> ListUnitsAsync(
        IDirectoryService directoryService,
        [FromServices] IUnitSubunitMembershipRepository subunitRepository,
        [FromQuery(Name = "display_name")] string? displayName,
        [FromQuery(Name = "parent_id")] string? parentId,
        CancellationToken cancellationToken)
    {
        var entries = await directoryService.ListAllAsync(cancellationToken);

        var unitEntries = entries
            .Where(e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // #1649: server-side display_name + parent_id filtering. Same shape
        // and acceptance as the agents list (see ListAgentsAsync). The
        // parent_id constraint walks the parent → child edge projection
        // (#1154) so the result is "direct children of this unit" — the
        // grandparent / multi-hop case is intentionally out of scope; the
        // CLI's `--unit` flag scopes to the immediate parent. Wire form is
        // the canonical no-dash hex but we accept dashed for parity with
        // GuidFormatter.TryParse.
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var trimmed = displayName.Trim();
            unitEntries = unitEntries
                .Where(e => string.Equals(e.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(parentId))
        {
            if (Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(parentId, out var parentGuid))
            {
                var children = await subunitRepository.ListByParentAsync(parentGuid, cancellationToken);
                var childIds = new HashSet<Guid>(children.Select(c => c.ChildId));
                unitEntries = unitEntries
                    .Where(e => childIds.Contains(e.ActorId))
                    .ToList();
            }
            else
            {
                // Unparseable parent_id ⇒ no units satisfy the filter.
                unitEntries = new List<DirectoryEntry>();
            }
        }

        var units = unitEntries
            .Select(e => ToUnitResponse(e))
            .ToList();

        return Results.Ok(units);
    }

    private static async Task<IResult> GetUnitAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IToolGrantResolver toolGrantResolver,
        [FromServices] IAgentDefinitionProvider agentDefinitionProvider,
        [FromServices] IServiceScopeFactory scopeFactory,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");

        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(id, out var unitGuid))
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var address = new Address("unit", unitGuid);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var status = await TryGetLifecycleStatusAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);
        var metadata = await TryGetUnitMetadataAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);
        var validationTracking = await TryGetValidationTrackingAsync(
            scopeFactory, entry.ActorId, logger, id, cancellationToken);
        // #2293: surface the persisted `instructions` slot from the
        // unit definition so the portal's Instructions sub-tab can read
        // and overlay inherited values without a second round-trip.
        var instructions = await TryReadUnitInstructionsAsync(
            scopeFactory, entry.ActorId, logger, id, cancellationToken);

        // #339: Read the unit's status-query payload (status + member count)
        // by calling the actor proxy directly, bypassing the message router.
        // The router's permission gate is for external human-originated
        // dispatch — a platform-internal read path must not be refused just
        // because the hardcoded synthetic From lacks Viewer permission on
        // units created post-#328. The payload shape must stay byte-
        // compatible with UnitActor.HandleStatusQueryAsync so clients that
        // parse the Details envelope keep working.
        var details = await TryGetLifecycleStatusPayloadAsync(
            actorProxyFactory, entry.ActorId, logger, id, cancellationToken);

        // #2337 Sub D: resolve effective tools so the portal's Tools
        // sub-tab can render the three-tier layout without re-deriving
        // the grant set. Fail-open (empty list) — the helper logs and
        // swallows transient resolver failures.
        var effectiveTools = await AgentEndpoints.TryResolveEffectiveToolsAsync(
            toolGrantResolver,
            new Address(Address.UnitScheme, entry.ActorId),
            logger,
            id,
            cancellationToken);

        // #2348: surface the unit's effective execution.image tag via
        // IAgentDefinitionProvider — the same path the dispatcher uses
        // (which, for unit-as-agent, projects the unit's own definition
        // JSON). Fail-open (null) so a transient definition-store outage
        // doesn't blank the otherwise-complete response.
        var executionImage = await AgentEndpoints.TryResolveExecutionImageAsync(
            agentDefinitionProvider,
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId),
            logger,
            id,
            cancellationToken);

        var unitResponse = ToUnitResponse(entry, status, metadata, validationTracking, instructions, effectiveTools, executionImage);
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
    private static async Task<JsonElement?> TryGetLifecycleStatusPayloadAsync(
        IActorProxyFactory actorProxyFactory,
        Guid actorId,
        ILogger logger,
        string unitId,
        CancellationToken cancellationToken)
    {
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorId)), nameof(UnitActor));

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

    private static async Task<LifecycleStatus> TryGetLifecycleStatusAsync(
        IActorProxyFactory actorProxyFactory,
        Guid actorId,
        ILogger logger,
        string unitId,
        CancellationToken cancellationToken)
    {
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorId)), nameof(UnitActor));
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
            return LifecycleStatus.Draft;
        }
    }

    private static async Task<UnitMetadata> TryGetUnitMetadataAsync(
        IActorProxyFactory actorProxyFactory,
        Guid actorId,
        ILogger logger,
        string unitId,
        CancellationToken cancellationToken)
    {
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorId)), nameof(UnitActor));
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
            return new UnitMetadata(
                DisplayName: null,
                Description: null,
                Model: null,
                Color: null);
        }
    }

    private static async Task<IResult> CreateUnitAsync(
        CreateUnitRequest request,
        [FromServices] IUnitCreationService creationService,
        [FromServices] IActivityEventBus activityEventBus,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // #1632: reject Guid-shaped / empty / control-char display names up
        // front. The unit creation service treats DisplayName as opaque, so
        // the validation has to live on the endpoint boundary.
        var displayNameProblem = DisplayNameProblems.ValidateOrProblem(request.DisplayName);
        if (displayNameProblem is not null)
        {
            return displayNameProblem;
        }

        try
        {
            var result = await creationService.CreateAsync(request, cancellationToken);

            // #2528: emit a StateChanged event so the portal's SSE-driven
            // cache invalidation refreshes the tenant tree without a manual
            // reload. The clean-path delete below mirrors this; force-delete
            // already publishes its own event in PublishForceDeleteEventAsync.
            await PublishUnitLifecycleEventAsync(
                activityEventBus,
                loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints"),
                Address.For("unit", result.Unit.Name),
                $"Unit '{result.Unit.DisplayName}' created.",
                lifecyclePhase: "created",
                cancellationToken);

            return Results.Created(
                $"/api/v1/units/{Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(result.Unit.Id)}",
                result.Unit);
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
        HttpContext httpContext,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IServiceScopeFactory scopeFactory,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");

        // #2293: parse the body twice — once as the typed DTO for the
        // existing fields, and once as a raw JsonDocument so we can
        // distinguish "absent" from "explicit null" for the tri-state
        // `instructions` slot (set / clear / leave-alone). The typed
        // DTO collapses both shapes at deserialization.
        var jsonOptions = httpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value
            .SerializerOptions;

        UpdateUnitRequest? request;
        InstructionsPatch instructionsPatch;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                httpContext.Request.Body, cancellationToken: cancellationToken);
            request = document.RootElement.Deserialize<UpdateUnitRequest>(jsonOptions);
            instructionsPatch = ReadInstructionsPatch(document.RootElement);
        }
        catch (JsonException ex)
        {
            return Results.Problem(
                detail: $"Request body is not valid JSON: {ex.Message}",
                statusCode: StatusCodes.Status400BadRequest);
        }

        request ??= new UpdateUnitRequest();

        // #1632: validate only when the caller actually supplied a new
        // display name — null means "leave unchanged" on the PATCH surface
        // and must stay a no-op for the validator.
        if (request.DisplayName is not null)
        {
            var displayNameProblem = DisplayNameProblems.ValidateOrProblem(request.DisplayName);
            if (displayNameProblem is not null)
            {
                return displayNameProblem;
            }
        }

        var address = Address.For("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // DisplayName / Description / Role live on the directory entity — route
        // those through IDirectoryService (#123 + #2341). Model / Color / Hosting /
        // Specialty / Enabled / ExecutionMode are actor-owned and persisted
        // through SetMetadataAsync. We always forward the PATCH to the actor so
        // the audit trail captures the change even when only directory-side
        // fields are touched.
        if (request.DisplayName is not null || request.Description is not null || request.Role is not null)
        {
            var updatedEntry = await directoryService.UpdateEntryAsync(
                address,
                request.DisplayName,
                request.Description,
                role: request.Role,
                cancellationToken: cancellationToken);

            entry = updatedEntry ?? entry;
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(UnitActor));

        // ADR-0038: Provider was dropped from the unit-metadata wire shape;
        // the provider is intrinsic to the structured execution.model.
        var metadata = new UnitMetadata(
            DisplayName: request.DisplayName,
            Description: request.Description,
            Model: request.Model,
            Color: request.Color,
            Provider: null,
            Hosting: request.Hosting,
            Specialty: request.Specialty,
            Enabled: request.Enabled,
            ExecutionMode: request.ExecutionMode);

        await proxy.SetMetadataAsync(metadata, cancellationToken);

        // #2293: when the caller addressed the `instructions` slot, apply
        // the patch in place on the persisted Definition JSON. Read-
        // modify-write preserves every sibling property (mirrors the
        // expertise precedent in UnitCreationService).
        if (instructionsPatch.IsPresent)
        {
            await ApplyUnitInstructionsPatchAsync(
                scopeFactory, entry.ActorId, logger, id, instructionsPatch.Value, cancellationToken);
        }

        var status = await TryGetLifecycleStatusAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);
        var updatedMetadata = await TryGetUnitMetadataAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);
        var instructions = await TryReadUnitInstructionsAsync(
            scopeFactory, entry.ActorId, logger, id, cancellationToken);

        return Results.Ok(ToUnitResponse(entry, status, updatedMetadata, validationTracking: null, instructions));
    }

    /// <summary>
    /// Tri-state for the optional <c>instructions</c> slot on PATCH bodies.
    /// </summary>
    /// <param name="IsPresent">
    /// <c>true</c> when the request body carried the property (with any
    /// value, including JSON <c>null</c>). <c>false</c> means the property
    /// was absent — the slot should be left unchanged.
    /// </param>
    /// <param name="Value">
    /// The new value to persist when <see cref="IsPresent"/> is <c>true</c>.
    /// <c>null</c> means "clear the slot"; a string means "replace".
    /// </param>
    private readonly record struct InstructionsPatch(bool IsPresent, string? Value)
    {
        public static InstructionsPatch Absent => new(false, null);
    }

    /// <summary>
    /// Inspects the raw request body for the <c>instructions</c> property
    /// and returns the corresponding patch tri-state. Property lookup is
    /// case-insensitive so wire forms produced by JS / C# clients both work.
    /// </summary>
    private static InstructionsPatch ReadInstructionsPatch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return InstructionsPatch.Absent;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "instructions", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return prop.Value.ValueKind switch
            {
                JsonValueKind.Null => new InstructionsPatch(true, null),
                JsonValueKind.String => new InstructionsPatch(true, prop.Value.GetString()),
                _ => InstructionsPatch.Absent,
            };
        }

        return InstructionsPatch.Absent;
    }

    /// <summary>
    /// Applies the <c>instructions</c> tri-state to the unit's persisted
    /// <c>UnitDefinitions.Definition</c> column. Preserves every sibling
    /// property (mirrors the expertise read-modify-write precedent in
    /// <c>UnitCreationService</c>). <c>null</c> removes the key; a string
    /// replaces it.
    /// </summary>
    private static async Task ApplyUnitInstructionsPatchAsync(
        IServiceScopeFactory scopeFactory,
        Guid unitActorId,
        ILogger logger,
        string unitId,
        string? value,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var entity = await db.UnitDefinitions
                .FirstOrDefaultAsync(u => u.Id == unitActorId && u.DeletedAt == null, cancellationToken);

            if (entity is null)
            {
                logger.LogWarning(
                    "Unit '{UnitId}': no UnitDefinition row found while applying instructions patch; skipping.",
                    unitId);
                return;
            }

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (entity.Definition is { ValueKind: JsonValueKind.Object } existing)
            {
                foreach (var prop in existing.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "instructions", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    payload[prop.Name] = prop.Value;
                }
            }

            if (value is not null)
            {
                payload["instructions"] = value;
            }

            entity.Definition = JsonSerializer.SerializeToElement(payload);
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Unit '{UnitId}': failed to apply instructions patch on UnitDefinition.",
                unitId);
        }
    }

    /// <summary>
    /// Reads the unit's persisted <c>instructions</c> string from the
    /// <c>UnitDefinitions.Definition</c> JSON. Returns <c>null</c> when the
    /// row, the document, or the property is missing — surfaces as "no own
    /// instructions" on the response.
    /// </summary>
    private static async Task<string?> TryReadUnitInstructionsAsync(
        IServiceScopeFactory scopeFactory,
        Guid unitActorId,
        ILogger logger,
        string unitId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var entity = await db.UnitDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == unitActorId && u.DeletedAt == null, cancellationToken);

            if (entity?.Definition is { ValueKind: JsonValueKind.Object } definition
                && definition.TryGetProperty("instructions", out var prop)
                && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Unit '{UnitId}': failed to read instructions from UnitDefinition.",
                unitId);
            return null;
        }
    }

    private static async Task<IResult> DeleteUnitAsync(
        string id,
        [FromQuery] bool? force,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        [FromServices] IUnitConnectorConfigStore connectorConfigStore,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromServices] IExecutionHostGateway executionGateway,
        [FromServices] IActivityEventBus activityEventBus,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");
        var address = Address.For("unit", id);
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
        var status = await TryGetLifecycleStatusAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);
        var isForce = force == true;

        if (!isForce && status != LifecycleStatus.Draft && status != LifecycleStatus.Stopped)
        {
            return Results.Conflict(new
            {
                Error = $"Unit '{id}' is {status}; stop it before deleting.",
                CurrentStatus = status,
                Hint = $"POST /api/v1/units/{id}/stop",
                ForceHint = $"DELETE /api/v1/units/{id}?force=true bypasses the gate for stuck units.",
            });
        }

        if (!isForce || status == LifecycleStatus.Draft || status == LifecycleStatus.Stopped)
        {
            // Clean-path delete. No runtime teardown required — the gate above
            // already proved the container / sidecar / webhook are either gone
            // or never existed.
            var displayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? id : entry.DisplayName;
            await directoryService.UnregisterAsync(address, cancellationToken);

            // #2528: emit a StateChanged event so the portal's SSE-driven
            // tree refresh fires without a manual reload. Force-delete has
            // its own emission in PublishForceDeleteEventAsync.
            await PublishUnitLifecycleEventAsync(
                activityEventBus,
                logger,
                address,
                $"Unit '{displayName}' deleted.",
                lifecyclePhase: "deleted",
                cancellationToken);

            return Results.NoContent();
        }

        return await ForceDeleteUnitAsync(
            id, address, entry.ActorId, status,
            directoryService, connectorTypes, connectorConfigStore,
            membershipRepository, executionGateway,
            activityEventBus, logger, cancellationToken);
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
        Guid actorId,
        LifecycleStatus previousStatus,
        IDirectoryService directoryService,
        IEnumerable<IConnectorType> connectorTypes,
        IUnitConnectorConfigStore connectorConfigStore,
        IUnitMembershipRepository membershipRepository,
        IExecutionHostGateway executionGateway,
        IActivityEventBus activityEventBus,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Force-delete requested for unit {UnitId} in status {Status}. Performing best-effort teardown.",
            id, previousStatus);

        var failures = new List<string>();

        try
        {
            // Delegate to the connector type owning this unit so it can
            // tear down its external resources. Each connector's stop hook
            // is responsible for catching its own errors; the try/catch
            // here is a second safety net. ADR-0040 / #2050: the binding
            // lookup is an EF read keyed by the unit's actor Guid.
            await DispatchConnectorStopAsync(
                Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorId),
                connectorConfigStore, connectorTypes, logger, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Force-delete: connector teardown failed for unit {UnitId}.", id);
            failures.Add("connector");
        }

        try
        {
            // #2627: unit-container teardown is delegated to the execution
            // host (spring-worker) over the gateway — the API host no longer
            // resolves IUnitContainerLifecycle (or any execution service).
            await executionGateway.StopUnitContainerAsync(
                Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorId), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Force-delete: container teardown failed for unit {UnitId}.", id);
            failures.Add("container");
        }

        try
        {
            // #2397: persistent-agent members own their own containers + Dapr
            // sidecars; the unit-level teardown above does not touch them.
            await UndeployPersistentAgentMembersAsync(
                actorId, membershipRepository, executionGateway, logger, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Force-delete: persistent-agent member teardown failed for unit {UnitId}.", id);
            failures.Add("persistent-agent-members");
        }

        try
        {
            // #2708: a unit-as-agent (ADR-0039) runs its own router runtime
            // under the unit's id as a persistent agent. The members cascade
            // above never enumerates this runtime — it isn't in the unit's
            // members collection — so a separate undeploy is needed to drop
            // the container and reclaim the workspace volume (which still
            // holds the agent's live credentials). Idempotent: a no-op for
            // ephemeral units or for units whose router was never deployed.
            await UndeployUnitAsAgentRuntimeAsync(
                actorId, executionGateway, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Force-delete: unit-as-agent router teardown failed for unit {UnitId}.", id);
            failures.Add("persistent-agent-router");
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
                UnitId: actorId,
                ForceDeleted: true,
                PreviousStatus: previousStatus,
                TeardownFailures: failures,
                Message: "Directory entry removed; some teardown steps failed — inspect operator logs and the activity stream."));
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Emits a lightweight <see cref="ActivityEventType.StateChanged"/> event
    /// on the unit's address so the portal's SSE-driven cache invalidation
    /// (see <c>queryKeysAffectedBySource</c> in
    /// <c>src/lib/api/query-keys.ts</c>) refreshes the tenant tree without
    /// a manual reload (#2528). Failures are logged and swallowed —
    /// observability emission must never gate the API write.
    /// </summary>
    private static async Task PublishUnitLifecycleEventAsync(
        IActivityEventBus bus,
        ILogger logger,
        Address unit,
        string summary,
        string lifecyclePhase,
        CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                lifecyclePhase,
            }));

            var evt = new ActivityEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                unit,
                ActivityEventType.StateChanged,
                ActivitySeverity.Info,
                summary,
                doc.RootElement.Clone());

            await bus.PublishAsync(evt, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to publish lifecycle activity event for unit {Unit} ({Phase}).",
                unit.Path, lifecyclePhase);
        }
    }

    private static async Task PublishForceDeleteEventAsync(
        IActivityEventBus bus,
        Address unit,
        LifecycleStatus previousStatus,
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
        var address = Address.For("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(UnitActor));

        var readiness = await proxy.CheckReadinessAsync(cancellationToken);
        return Results.Ok(new UnitReadinessResponse(readiness.IsReady, readiness.MissingRequirements));
    }

    private static async Task<IResult> StartUnitAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitConnectorStartDispatcher startDispatcher,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");
        var address = Address.For("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(UnitActor));

        var startingTransition = await proxy.TransitionAsync(LifecycleStatus.Starting, cancellationToken);
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
        // #2156: the dispatch body lives in IUnitConnectorStartDispatcher so
        // the actor's post-validation auto-start path can run the same hook
        // without depending on UnitEndpoints internals.
        await startDispatcher.DispatchAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId),
            cancellationToken);

        // Transition straight to Running. Agent-container lifecycle is
        // managed by the A2A dispatcher (#346/#349), not by this endpoint.
        var runningTransition = await proxy.TransitionAsync(LifecycleStatus.Running, cancellationToken);
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
            new UnitLifecycleResponse(entry.ActorId, runningTransition.CurrentStatus));
    }

    private static async Task<IResult> StopUnitAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IEnumerable<IConnectorType> connectorTypes,
        [FromServices] IUnitConnectorConfigStore connectorConfigStore,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromServices] IExecutionHostGateway executionGateway,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");
        var address = Address.For("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(UnitActor));

        var stoppingTransition = await proxy.TransitionAsync(LifecycleStatus.Stopping, cancellationToken);
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
        // block the /stop flow. ADR-0040 / #2050: binding lookup goes
        // through the EF-backed config store.
        await DispatchConnectorStopAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId),
            connectorConfigStore, connectorTypes, logger, cancellationToken);

        // Undeploy any persistent-agent members so their per-agent containers
        // + Dapr sidecars do not survive the unit's Stopped state (#2397).
        // The A2A dispatcher only reaps ephemeral per-conversation containers;
        // persistent deployments hang on the agent registry until something
        // calls UndeployAsync. Best-effort — a lookup failure logs and lets
        // the unit transition to Stopped, mirroring the connector-dispatch
        // pattern above; the operator can fall back to force-delete.
        try
        {
            await UndeployPersistentAgentMembersAsync(
                entry.ActorId, membershipRepository, executionGateway, logger, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Persistent-agent member teardown failed for unit {UnitId}; continuing unit stop.",
                id);
        }

        // #2708: a unit-as-agent (ADR-0039) runs its own router runtime under
        // the unit's id. The members cascade above never enumerates it, so a
        // separate undeploy is needed before the unit transitions to Stopped
        // — without this, the container and its workspace volume (which
        // still holds the agent's live credentials) survive the stop and the
        // subsequent clean-path delete leaks them. Idempotent: a no-op for
        // ephemeral units or for units whose router was never deployed.
        try
        {
            await UndeployUnitAsAgentRuntimeAsync(
                entry.ActorId, executionGateway, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unit-as-agent router teardown failed for unit {UnitId}; continuing unit stop.",
                id);
        }

        var stoppedTransition = await proxy.TransitionAsync(LifecycleStatus.Stopped, cancellationToken);
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
            new UnitLifecycleResponse(entry.ActorId, stoppedTransition.CurrentStatus));
    }

    /// <summary>
    /// Handler for <c>POST /api/v1/units/{id}/revalidate</c>. Allowed
    /// from <see cref="LifecycleStatus.Draft"/>, <see cref="LifecycleStatus.Error"/>,
    /// or <see cref="LifecycleStatus.Stopped"/> — every state from which the
    /// actor's transition table allows entering
    /// <see cref="LifecycleStatus.Validating"/>. <c>Draft</c> covers the
    /// first-time validation path the wizard's <c>Validate</c> button
    /// drives when the create endpoint left the unit in Draft (the
    /// credential-free / no-credential runtime case, e.g. Ollama),
    /// per #1451. Any other status returns 409 with a structured
    /// <c>currentStatus</c> detail so the client can surface guidance.
    /// The handler returns 202 immediately; the workflow's terminal
    /// activity drives the follow-up <see cref="LifecycleStatus.Validating"/> →
    /// <see cref="LifecycleStatus.Stopped"/> or <see cref="LifecycleStatus.Error"/>
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

        var address = Address.For("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var status = await TryGetLifecycleStatusAsync(actorProxyFactory, entry.ActorId, logger, id, cancellationToken);
        if (status != LifecycleStatus.Draft && status != LifecycleStatus.Error && status != LifecycleStatus.Stopped)
        {
            return Results.Problem(
                title: "Invalid state",
                detail: $"Unit '{id}' is {status}; revalidation is only allowed from Draft, Error, or Stopped.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "InvalidState",
                    ["currentStatus"] = status.ToString(),
                });
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(UnitActor));

        var transition = await proxy.TransitionAsync(LifecycleStatus.Validating, cancellationToken);
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
        var unitAddress = Address.For("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(UnitActor));
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
        IExecutionConfigInheritanceResolver inheritanceResolver,
        IUnitSubunitMembershipRepository subunitRepository,
        IUnitExecutionStore unitExecutionStore,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var unitAddress = Address.For("unit", id);
        var entry = await directoryService.ResolveAsync(unitAddress, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var memberAddress = Address.For(request.MemberAddress.Scheme, request.MemberAddress.Path);

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

        // ADR-0039 §6 / B5: when assigning a unit-as-member (sub-unit), the
        // child's effective execution config must remain consistent against
        // the *new* expanded parent set. If the child inherits a field that
        // diverges across the post-assignment parents, reject with 422 and a
        // structured `MultiParentInheritanceConflict` body so the operator
        // either pins the field on the child or removes a conflicting parent.
        // Agent-as-member assignments go through B3's dedicated handler
        // (AssignUnitAgentAsync) — this branch is sub-unit-only.
        if (string.Equals(memberAddress.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            var conflictResult = await CheckSubunitInheritanceAsync(
                parentUnitId: entry.ActorId,
                childUnitAddress: memberAddress,
                directoryService,
                inheritanceResolver,
                subunitRepository,
                unitExecutionStore,
                tenantContext,
                cancellationToken);

            if (conflictResult is not null)
            {
                return conflictResult;
            }
        }

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(UnitActor));

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
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IExpertiseAggregator expertiseAggregator,
        [FromServices] IUnitParentInvariantGuard parentGuard,
        [FromServices] IExecutionConfigInheritanceResolver inheritanceResolver,
        [FromServices] IUnitSubunitMembershipRepository subunitRepository,
        [FromServices] IUnitExecutionStore unitExecutionStore,
        [FromServices] ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var unitAddress = Address.For("unit", id);
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
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(UnitActor));

        // ADR-0039 §6 / B6: if this member id is a sub-unit, removing the
        // edge reshapes the child unit's parent set. Resolve against the
        // remaining parents before the actor-state write so an inherited
        // field that still diverges returns the same structured 422 used by
        // sub-unit assignment.
        var childUnitAddress = Address.For(Address.UnitScheme, memberId);
        var childUnitEntry = await directoryService.ResolveAsync(childUnitAddress, cancellationToken);
        if (childUnitEntry is not null)
        {
            var conflictResult = await CheckSubunitUnassignmentInheritanceAsync(
                parentUnitId: entry.ActorId,
                childUnitId: childUnitEntry.ActorId,
                inheritanceResolver,
                subunitRepository,
                unitExecutionStore,
                tenantContext,
                cancellationToken);

            if (conflictResult is not null)
            {
                return conflictResult;
            }
        }

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
                Address.For("unit", memberId),
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

        await unitProxy.RemoveMemberAsync(Address.For("agent", memberId), cancellationToken);
        await unitProxy.RemoveMemberAsync(Address.For("unit", memberId), cancellationToken);

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
        IHumanIdentityResolver identityResolver,
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

        // Resolve the incoming username (from the URL path) to a stable UUID.
        // On first contact this upserts a row in the humans table so the
        // UUID is stable for the lifetime of the deployment.
        var humanGuid = await identityResolver.ResolveByUsernameAsync(
            humanId, request.Identity, cancellationToken);

        var permissionEntry = new UnitPermissionEntry(
            humanGuid.ToString(),
            permissionLevel,
            request.Identity,
            request.Notifications ?? true);

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(auth.Entry!.ActorId)), nameof(UnitActor));

        await unitProxy.SetHumanPermissionAsync(humanGuid, permissionEntry, cancellationToken);

        // #2044 / ADR-0040: ACL grants are now EF rows. The dual write to
        // HumanActor.SetPermissionForUnitAsync (Human:UnitPermissions actor
        // state) was the cause of the dual-storage hazard called out in
        // #2032 — unit_human_permissions is the single source of truth.

        return Results.Ok(new SetHumanPermissionResponse(humanGuid, permissionLevel));
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
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(auth.Entry!.ActorId)), nameof(UnitActor));

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
        IHumanIdentityResolver identityResolver,
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

        // Resolve the username to its stable UUID. If no UUID exists yet,
        // ResolveByUsernameAsync upserts one — the remove that follows will
        // simply find an empty permission map and return false (idempotent).
        var humanGuid = await identityResolver.ResolveByUsernameAsync(
            humanId, null, cancellationToken);

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(auth.Entry!.ActorId)), nameof(UnitActor));

        await unitProxy.RemoveHumanPermissionAsync(humanGuid, cancellationToken);

        // #2044 / ADR-0040: ACL grants are now EF rows; the dual delete
        // against HumanActor.RemovePermissionForUnitAsync is gone.
        // unit_human_permissions is the single source of truth.

        return Results.NoContent();
    }

    /// <summary>
    /// Mirrors <see cref="IUnitConnectorStartDispatcher"/> for the stop path
    /// (the start path was extracted in #2156 so it can be reused from the
    /// unit actor's auto-start hook; the stop path is endpoint-only today).
    /// </summary>
    /// <summary>
    /// Iterates every member of the unit and undeploys any persistent-agent
    /// deployment so the per-agent container + Dapr sidecar do not survive
    /// the unit's Stopped state (#2397). Best-effort per agent: an undeploy
    /// failure on one member is logged and recorded but does not block the
    /// others, mirroring the per-step pattern in <see cref="ForceDeleteUnitAsync"/>.
    /// <para>
    /// <see cref="IExecutionHostGateway.UndeployAsync"/> is
    /// idempotent — it is a no-op when nothing is deployed — so non-persistent
    /// agents (ephemeral runtime, or persistent agents that were never
    /// deployed) cost only a delegated lookup on the execution host.
    /// </para>
    /// </summary>
    private static async Task UndeployPersistentAgentMembersAsync(
        Guid unitActorId,
        IUnitMembershipRepository membershipRepository,
        IExecutionHostGateway executionGateway,
        ILogger logger,
        CancellationToken ct)
    {
        var memberships = await membershipRepository.ListByUnitAsync(unitActorId, ct);
        if (memberships.Count == 0)
        {
            return;
        }

        foreach (var membership in memberships)
        {
            var agentId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(membership.AgentId);
            try
            {
                await executionGateway.UndeployAsync(agentId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Undeploy failed for persistent-agent member {AgentId} of unit {UnitId}; continuing unit stop.",
                    agentId, Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitActorId));
            }
        }
    }

    /// <summary>
    /// Undeploys the unit-as-agent's own router runtime (#2708). A unit-as-agent
    /// (ADR-0039) deploys under the unit's id via
    /// <c>PersistentAgentLifecycle.DeployAsync</c>; that container does not
    /// appear in the unit's <c>members:</c> collection, so the per-member
    /// cascade in <see cref="UndeployPersistentAgentMembersAsync"/> never
    /// touches it. Calling
    /// <see cref="IExecutionHostGateway.UndeployAsync"/> with the unit's id
    /// drops both the router container and the per-agent workspace volume
    /// (which still holds the agent's live credentials). The gateway is
    /// idempotent: a no-op when no runtime is tracked (ephemeral units, or
    /// persistent units whose router was never deployed).
    /// </summary>
    private static Task UndeployUnitAsAgentRuntimeAsync(
        Guid unitActorId,
        IExecutionHostGateway executionGateway,
        CancellationToken ct)
    {
        var unitId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitActorId);
        return executionGateway.UndeployAsync(unitId, ct);
    }

    private static async Task DispatchConnectorStopAsync(
        string unitId,
        IUnitConnectorConfigStore configStore,
        IEnumerable<IConnectorType> connectorTypes,
        ILogger logger,
        CancellationToken ct)
    {
        var binding = await configStore.GetAsync(unitId, ct);
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
        LifecycleStatus status = LifecycleStatus.Draft,
        UnitMetadata? metadata = null,
        ArtefactValidationTracking? validationTracking = null,
        string? instructions = null,
        IReadOnlyList<EffectiveToolResponse>? effectiveTools = null,
        string? executionImage = null) =>
        new(
            entry.ActorId,
            entry.Address.Path,
            entry.DisplayName,
            entry.Description,
            entry.RegisteredAt,
            status,
            metadata?.Model,
            metadata?.Color,
            metadata?.Hosting,
            validationTracking?.LastValidationError,
            validationTracking?.LastValidationRunId,
            instructions,
            // #2341: directory-owned role + actor-owned parity fields.
            Role: entry.Role,
            Specialty: metadata?.Specialty,
            // Enabled / ExecutionMode are non-nullable on the response so that
            // UI callers can read them without null checks; the unit defaults
            // are Enabled=true and ExecutionMode=Auto, matching the agent contract.
            Enabled: metadata?.Enabled ?? true,
            ExecutionMode: metadata?.ExecutionMode ?? Cvoya.Spring.Core.Agents.AgentExecutionMode.Auto,
            // #2337 Sub D: effective-tools projection used by the portal's
            // Tools sub-tab. Only the single-subject GET path populates
            // this; list / other paths leave it as an empty list.
            EffectiveTools: effectiveTools ?? Array.Empty<EffectiveToolResponse>(),
            // #2348: execution.image tag from the persisted definition
            // (read via IAgentDefinitionProvider). Null when the unit
            // declares no image or when the list-path caller did not
            // resolve it.
            ExecutionImage: executionImage);

    /// <summary>
    /// View of the per-unit validation-tracking columns projected into the
    /// GET DTO. Parsed once per read via
    /// <see cref="TryGetValidationTrackingAsync"/> so the endpoint does not
    /// repeat the JSON parse in multiple code paths.
    /// </summary>
    private sealed record ArtefactValidationTracking(
        ArtefactValidationError? LastValidationError,
        string? LastValidationRunId);

    /// <summary>
    /// Reads the unit's <c>LastValidationErrorJson</c> / <c>LastValidationRunId</c>
    /// columns via a scoped <see cref="SpringDbContext"/> and returns a
    /// parsed view suitable for projection into <see cref="UnitResponse"/>.
    /// Returns <c>null</c> when the row is missing or the context is not
    /// registered (design-time / doc-gen path) so the DTO's null values
    /// surface naturally.
    /// </summary>
    private static async Task<ArtefactValidationTracking?> TryGetValidationTrackingAsync(
        IServiceScopeFactory scopeFactory,
        Guid actorId,
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
                .Where(u => u.Id == actorId && u.DeletedAt == null)
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

            ArtefactValidationError? error = null;
            if (!string.IsNullOrWhiteSpace(row.LastValidationErrorJson))
            {
                try
                {
                    error = JsonSerializer.Deserialize<ArtefactValidationError>(
                        row.LastValidationErrorJson);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Unit {UnitId}: failed to parse LastValidationErrorJson; omitting from response.",
                        unitId);
                }
            }

            return new ArtefactValidationTracking(error, row.LastValidationRunId);
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
        var unitAddress = Address.For("unit", id);
        var unitEntry = await directoryService.ResolveAsync(unitAddress, cancellationToken);

        if (unitEntry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitEntry.ActorId)), nameof(UnitActor));
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
                new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(AgentActor));
            var (metadata, parentUnitGuid) = await AgentEndpoints.GetDerivedAgentMetadataAsync(
                proxy, membershipRepository, entry.ActorId, directoryService, cancellationToken);
            responses.Add(AgentEndpoints.ToAgentResponse(entry, metadata, parentUnitId: parentUnitGuid));
        }

        return Results.Ok(responses);
    }

    private static async Task<IResult> AssignUnitAgentAsync(
        string id,
        string agentId,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromServices] IAgentExecutionStore agentExecutionStore,
        [FromServices] IExecutionConfigInheritanceResolver inheritanceResolver,
        [FromServices] ITenantContext tenantContext,
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

        var unitAddress = Address.For("unit", id);
        var unitEntry = await directoryService.ResolveAsync(unitAddress, cancellationToken);
        if (unitEntry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var agentAddress = Address.For("agent", agentId);
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

        // #1492: resolve slugs → UUIDs at the boundary. Both entries resolved above.
        var unitAssignUuid = unitEntry.ActorId;

        var agentAssignUuid = agentEntry.ActorId;

        // ADR-0039 §6 / B3: resolve the agent's effective execution config
        // against its post-assignment parent set. Adding the agent to this
        // unit can expand the parent set into a state where an inherited
        // field diverges across parents — when it does, reject with 422 so
        // the operator either trims the parent set or sets the field
        // explicitly on the agent. We run the resolver before any state
        // write so a rejection leaves no half-applied membership row.
        var parentSet = await BuildPostAssignmentParentSetAsync(
            unitAssignUuid,
            agentAssignUuid,
            membershipRepository,
            cancellationToken);

        var conflictResult = await CheckMultiParentInheritanceConflictAsync(
            parentSet,
            agentAssignUuid,
            agentExecutionStore,
            inheritanceResolver,
            tenantContext,
            cancellationToken);
        if (conflictResult is not null)
        {
            return conflictResult;
        }

        // C2b-1: M:N membership model (see #160). An agent may be a member
        // of multiple units. No 1:N conflict check — the old guard is gone
        // and operators may freely add the same agent to several units.
        // Existing membership rows are preserved (idempotent re-assign).
        //
        // #2072: route the write through UnitActor.AddMemberAsync — the
        // canonical membership-write surface post-#2052. The actor calls
        // UnitMembershipCoordinator, which idempotently writes the
        // unit_memberships row through IUnitMemberGraphStore and emits the
        // StateChanged/MemberAdded activity event. The previous
        // direct-repository upsert here was a redundant second write to
        // the same EF row.
        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitEntry.ActorId)), nameof(UnitActor));
        await unitProxy.AddMemberAsync(agentAddress, cancellationToken);

        // Also sync the legacy cached pointer on the agent actor so any
        // reader still relying on it sees a consistent value. The
        // authoritative source is the membership table.
        var agentProxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(agentEntry.ActorId)), nameof(AgentActor));
        await agentProxy.SetMetadataAsync(
            new AgentMetadata(ParentUnit: id),
            cancellationToken);

        // Membership change reshapes the unit's effective expertise and,
        // transitively, every ancestor unit's aggregated view (#412).
        await expertiseAggregator.InvalidateAsync(unitAddress, cancellationToken);

        logger.LogInformation(
            "Agent {AgentId} assigned to unit {UnitId}.", agentId, id);

        var (refreshed, refreshedParentUnitGuid) = await AgentEndpoints.GetDerivedAgentMetadataAsync(
            agentProxy, membershipRepository, agentAssignUuid, directoryService, cancellationToken);
        return Results.Ok(AgentEndpoints.ToAgentResponse(agentEntry, refreshed, parentUnitId: refreshedParentUnitGuid));
    }

    /// <summary>
    /// Builds the ADR-0039 §6 parent set for a prospective assignment: the
    /// agent's existing memberships plus <paramref name="newUnitId"/>.
    /// </summary>
    private static async Task<List<Guid>> BuildPostAssignmentParentSetAsync(
        Guid newUnitId,
        Guid agentId,
        IUnitMembershipRepository membershipRepository,
        CancellationToken cancellationToken)
    {
        var memberships = await membershipRepository.ListByAgentAsync(agentId, cancellationToken);
        var parentSet = new List<Guid>(memberships.Count + 1);
        var seen = new HashSet<Guid>();
        foreach (var m in memberships)
        {
            if (seen.Add(m.UnitId))
            {
                parentSet.Add(m.UnitId);
            }
        }
        if (seen.Add(newUnitId))
        {
            parentSet.Add(newUnitId);
        }

        return parentSet;
    }

    /// <summary>
    /// Runs the ADR-0039 §6 multi-parent inheritance resolver against an
    /// already-computed parent set. Returns a 422
    /// <c>MultiParentInheritanceConflict</c> response when an inherited
    /// field diverges across parents; returns <c>null</c> when resolution
    /// succeeds.
    /// </summary>
    /// <remarks>
    /// The agent's "own" config is read from
    /// <see cref="IAgentExecutionStore"/>; an agent with no persisted
    /// execution block is fully inheriting (every field is null), which
    /// is the common case operators hit when wiring an existing agent
    /// into a second unit. Hosting is agent-owned and never participates
    /// in the conflict map (the resolver excludes it by contract).
    /// </remarks>
    private static async Task<IResult?> CheckMultiParentInheritanceConflictAsync(
        IReadOnlyList<Guid> parentSet,
        Guid agentId,
        IAgentExecutionStore agentExecutionStore,
        IExecutionConfigInheritanceResolver inheritanceResolver,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parentSet);

        var ownShape = await agentExecutionStore.GetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(agentId),
            cancellationToken);
        var agentOwn = ToAgentExecutionConfig(ownShape);

        var resolution = await inheritanceResolver.ResolveAgentConfigAsync(
            agentOwn,
            parentSet,
            tenantContext.CurrentTenantId,
            cancellationToken);

        if (resolution.ConflictingFields.Count == 0)
        {
            return null;
        }

        return Results.Json(
            BuildMultiParentInheritanceConflictBody(resolution),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>
    /// Maps an <see cref="AgentExecutionShape"/> read from
    /// <see cref="IAgentExecutionStore"/> onto the
    /// <see cref="AgentExecutionConfig"/> shape the inheritance resolver
    /// expects. A null shape (agent has never declared its own execution
    /// block) becomes an all-null config — every field is a candidate for
    /// inheritance.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentExecutionConfig.Runtime"/> is non-nullable on
    /// the record but the resolver normalises empty strings via
    /// <c>NullIfBlank</c>, so passing an empty string is equivalent to
    /// "no agent runtime declared" for the conflict-detection rules.
    /// </remarks>
    private static AgentExecutionConfig ToAgentExecutionConfig(AgentExecutionShape? shape)
    {
        if (shape is null)
        {
            return new AgentExecutionConfig(Runtime: string.Empty, Image: null);
        }

        return new AgentExecutionConfig(
            Runtime: shape.Runtime ?? string.Empty,
            Image: shape.Image,
            Model: shape.Model,
            // #2691 / #2692: include the agent's declared system_prompt_mode
            // so the inheritance resolver can intersect it against the
            // parent set. A blank / unknown wire literal lands as null —
            // the slot becomes a candidate for inheritance.
            SystemPromptMode: ParseSystemPromptMode(shape.SystemPromptMode));

        static Cvoya.Spring.Core.Catalog.SystemPromptMode? ParseSystemPromptMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            return value.Trim().ToLowerInvariant() switch
            {
                "append" => Cvoya.Spring.Core.Catalog.SystemPromptMode.Append,
                "replace" => Cvoya.Spring.Core.Catalog.SystemPromptMode.Replace,
                _ => null,
            };
        }
    }

    /// <summary>
    /// Shapes the wire body the CLI parses on a 422 <c>MultiParentInheritanceConflict</c>:
    /// <c>{ "error": "MultiParentInheritanceConflict", "conflictingFields": { field: [{source, value}, ...] } }</c>.
    /// Each <c>source</c> is the canonical Guid hex form so the CLI can
    /// resolve it back to a unit display name without round-tripping.
    /// </summary>
    private static object BuildMultiParentInheritanceConflictBody(InheritanceResolution resolution)
    {
        var fields = new Dictionary<string, IReadOnlyList<object>>(StringComparer.Ordinal);
        foreach (var (field, values) in resolution.ConflictingFields)
        {
            fields[field] = values
                .Select(v => (object)new
                {
                    source = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(v.Source),
                    value = v.Value,
                })
                .ToList();
        }

        return new
        {
            error = "MultiParentInheritanceConflict",
            conflictingFields = fields,
        };
    }

    private static async Task<IResult> UnassignUnitAgentAsync(
        string id,
        string agentId,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromServices] IAgentExecutionStore agentExecutionStore,
        [FromServices] IExecutionConfigInheritanceResolver inheritanceResolver,
        [FromServices] ITenantContext tenantContext,
        [FromServices] IExpertiseAggregator expertiseAggregator,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.UnitEndpoints");

        var unitAddress = Address.For("unit", id);
        var unitEntry = await directoryService.ResolveAsync(unitAddress, cancellationToken);
        if (unitEntry is null)
        {
            return Results.Problem(detail: $"Unit '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var agentAddress = Address.For("agent", agentId);
        var agentEntry = await directoryService.ResolveAsync(agentAddress, cancellationToken);
        if (agentEntry is null)
        {
            return Results.Problem(detail: $"Agent '{agentId}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // #1492: resolve slugs → UUIDs at the boundary.
        var unitUnassignUuid = unitEntry.ActorId;

        var agentUnassignUuid = agentEntry.ActorId;

        // ADR-0039 §6 / B4: removing this unit reshapes the agent's parent
        // set. Resolve against the *remaining* parents before any write; if
        // the agent still inherits a diverging field from those parents,
        // reject with the same structured 422 body assignment uses.
        var existingMemberships = await membershipRepository.ListByAgentAsync(
            agentUnassignUuid,
            cancellationToken);
        var remainingParentSet = new List<Guid>(existingMemberships.Count);
        var seenRemainingParents = new HashSet<Guid>();
        foreach (var membership in existingMemberships)
        {
            if (membership.UnitId == unitUnassignUuid)
            {
                continue;
            }

            if (seenRemainingParents.Add(membership.UnitId))
            {
                remainingParentSet.Add(membership.UnitId);
            }
        }

        var conflictResult = await CheckMultiParentInheritanceConflictAsync(
            remainingParentSet,
            agentUnassignUuid,
            agentExecutionStore,
            inheritanceResolver,
            tenantContext,
            cancellationToken);
        if (conflictResult is not null)
        {
            return conflictResult;
        }

        // #2072: route the delete through UnitActor.RemoveMemberAsync — the
        // canonical membership-write surface post-#2052. The actor calls
        // UnitMembershipCoordinator, which idempotently removes the
        // unit_memberships row through IUnitMemberGraphStore and emits the
        // StateChanged/MemberRemoved activity event. The previous
        // direct-repository delete here was a redundant second write to
        // the same EF row.
        //
        // The repository's last-membership guard (#744) is intentionally
        // not consulted here: ADR-0039 §6 / B4 explicitly permits the
        // DELETE endpoint to remove the final row and leave the agent
        // top-level (resolves against tenant defaults). Endpoints that
        // *do* need the guard (e.g.
        // DELETE /api/v1/tenant/units/{id}/memberships/{agentId}) call
        // IUnitMembershipRepository.DeleteAsync directly per the
        // IUnitMemberGraphStore.RemoveAgentMemberAsync contract.
        var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitEntry.ActorId)), nameof(UnitActor));
        await unitProxy.RemoveMemberAsync(agentAddress, cancellationToken);

        // Refresh the cached pointer on the agent actor. If any memberships
        // remain, the derivation rule (first by CreatedAt) picks the new
        // "primary" unit; if this was the last membership, clear the pointer.
        var remaining = await membershipRepository.ListByAgentAsync(agentUnassignUuid, cancellationToken);
        var agentProxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(agentEntry.ActorId)), nameof(AgentActor));
        if (remaining.Count == 0)
        {
            await agentProxy.ClearParentUnitAsync(cancellationToken);
        }
        else
        {
            // Resolve UUID → display name for the ParentUnit field (#1629).
            // ListAll warms the in-memory directory cache on first call so
            // this is O(1) for subsequent requests within the same process.
            var allEntries = await directoryService.ListAllAsync(cancellationToken);
            var primaryUnitEntry = allEntries.FirstOrDefault(
                e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase)
                     && e.ActorId == remaining[0].UnitId);
            var primaryUnitDisplay = primaryUnitEntry?.DisplayName ?? remaining[0].UnitId.ToString("N");
            await agentProxy.SetMetadataAsync(
                new AgentMetadata(ParentUnit: primaryUnitDisplay),
                cancellationToken);
        }

        // Invalidate the aggregator cache up the chain: removing the agent
        // changes this unit's effective expertise and every ancestor's.
        await expertiseAggregator.InvalidateAsync(unitAddress, cancellationToken);

        logger.LogInformation(
            "Agent {AgentId} unassigned from unit {UnitId}.", agentId, id);

        return Results.NoContent();
    }

    /// <summary>
    /// ADR-0039 §6 / B5: enforces multi-parent execution-config consistency
    /// when assigning a unit-as-member (sub-unit) to a parent unit. Resolves
    /// the child unit's effective config against the *post-assignment* parent
    /// set (existing parents ∪ new parent). If any inheritable field
    /// diverges and the child has not pinned it explicitly, returns a 422
    /// <c>MultiParentInheritanceConflict</c> result. Returns <c>null</c>
    /// when assignment may proceed.
    /// </summary>
    /// <remarks>
    /// The child's "own" config is its persisted <see cref="UnitExecutionDefaults"/>
    /// — the same five string slots the resolver consumes for agent
    /// inheritance. A unit that has not declared an execution block is
    /// treated as fully-inheriting (every field null), matching the
    /// resolver's per-field rule.
    /// </remarks>
    private static async Task<IResult?> CheckSubunitInheritanceAsync(
        Guid parentUnitId,
        Address childUnitAddress,
        IDirectoryService directoryService,
        IExecutionConfigInheritanceResolver inheritanceResolver,
        IUnitSubunitMembershipRepository subunitRepository,
        IUnitExecutionStore unitExecutionStore,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        // Resolve the child unit so we can key the existing-parents lookup
        // and the execution-store read by its actor id, not the URL slug.
        var childEntry = await directoryService.ResolveAsync(childUnitAddress, cancellationToken);
        if (childEntry is null)
        {
            // The actor's AddMemberAsync will surface the "unknown member"
            // failure; bypass the inheritance check rather than 404 here so
            // the failure mode stays consistent with the existing handler.
            return null;
        }

        // Build the post-assignment parent set: every current parent plus
        // the new parent. De-duplicate so an idempotent re-add does not
        // double-count.
        var existingParents = await subunitRepository.ListByChildAsync(childEntry.ActorId, cancellationToken);
        var parentIds = new List<Guid>(existingParents.Count + 1);
        var seen = new HashSet<Guid>();
        foreach (var edge in existingParents)
        {
            if (seen.Add(edge.ParentId))
            {
                parentIds.Add(edge.ParentId);
            }
        }
        if (seen.Add(parentUnitId))
        {
            parentIds.Add(parentUnitId);
        }

        // Project the child's persisted UnitExecutionDefaults onto the
        // resolver's AgentExecutionConfig shape. The inheritable slots map
        // 1:1 (Runtime, Image, Model); a unit with no defaults persisted
        // is treated as fully-inheriting.
        var childOwnDefaults = await unitExecutionStore.GetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(childEntry.ActorId),
            cancellationToken);

        return await CheckSubunitInheritanceParentSetAsync(
            childOwnDefaults,
            parentIds,
            inheritanceResolver,
            tenantContext,
            cancellationToken,
            "Sub-unit assignment would leave the child unit inheriting an inconsistent execution config across its parents.");
    }

    /// <summary>
    /// ADR-0039 §6 / B6: enforces multi-parent execution-config consistency
    /// when unassigning a unit-as-member (sub-unit) from a parent unit.
    /// Resolves the child unit's effective config against the remaining
    /// parent set after the target edge is removed.
    /// </summary>
    private static async Task<IResult?> CheckSubunitUnassignmentInheritanceAsync(
        Guid parentUnitId,
        Guid childUnitId,
        IExecutionConfigInheritanceResolver inheritanceResolver,
        IUnitSubunitMembershipRepository subunitRepository,
        IUnitExecutionStore unitExecutionStore,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var existingParents = await subunitRepository.ListByChildAsync(childUnitId, cancellationToken);
        var remainingParentIds = new List<Guid>(existingParents.Count);
        var seen = new HashSet<Guid>();
        foreach (var edge in existingParents)
        {
            if (edge.ParentId == parentUnitId)
            {
                continue;
            }

            if (seen.Add(edge.ParentId))
            {
                remainingParentIds.Add(edge.ParentId);
            }
        }

        if (remainingParentIds.Count == 0)
        {
            return null;
        }

        var childOwnDefaults = await unitExecutionStore.GetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(childUnitId),
            cancellationToken);

        return await CheckSubunitInheritanceParentSetAsync(
            childOwnDefaults,
            remainingParentIds,
            inheritanceResolver,
            tenantContext,
            cancellationToken,
            "Sub-unit unassignment would leave the child unit inheriting an inconsistent execution config across its remaining parents.");
    }

    private static async Task<IResult?> CheckSubunitInheritanceParentSetAsync(
        UnitExecutionDefaults? childOwnDefaults,
        IReadOnlyList<Guid> parentIds,
        IExecutionConfigInheritanceResolver inheritanceResolver,
        ITenantContext tenantContext,
        CancellationToken cancellationToken,
        string detail)
    {
        var childOwnConfig = new AgentExecutionConfig(
            Runtime: childOwnDefaults?.Runtime ?? string.Empty,
            Image: childOwnDefaults?.Image,
            Model: childOwnDefaults?.Model,
            // #2691 / #2692: include the child's declared
            // system_prompt_mode so the inheritance resolver intersects it
            // against the remaining parent set (same rule as the other
            // inheritable slots).
            SystemPromptMode: childOwnDefaults?.SystemPromptMode);

        var resolution = await inheritanceResolver.ResolveAgentConfigAsync(
            childOwnConfig,
            parentIds,
            tenantContext.CurrentTenantId,
            cancellationToken);

        if (resolution.ConflictingFields.Count == 0)
        {
            return null;
        }

        // Shape mirrors B1 (and the rest of the B-wave): a structured
        // `MultiParentInheritanceConflict` body so the CLI / portal can
        // print one line per diverging field with the per-parent values.
        var conflictingFields = resolution.ConflictingFields.ToDictionary(
            kvp => kvp.Key,
            kvp => (object?)kvp.Value
                .Select(pv => new { source = pv.Source, value = pv.Value })
                .ToArray());

        return Results.Problem(
            title: "Multi-parent inheritance conflict",
            detail: detail,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>
            {
                ["error"] = "MultiParentInheritanceConflict",
                ["conflictingFields"] = conflictingFields,
            });
    }

    // -------------------------------------------------------------------------
    // Unit deployment status (#2274). Returns whether the unit is running
    // and its current status label. Mirrors the agent deployment surface
    // so the portal's unified DeploymentTab renders the same controls.
    // -------------------------------------------------------------------------

    private static async Task<IResult> GetUnitDeploymentAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var address = Address.For("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)),
            nameof(UnitActor));

        var status = await proxy.GetStatusAsync(cancellationToken);
        return Results.Ok(new UnitDeploymentResponse(
            Running: status == LifecycleStatus.Running,
            Status: status.ToString()));
    }

    // -------------------------------------------------------------------------
    // Equipped skill bundles on a unit (#2360). The unit store feeds Layer 2
    // of the assembled prompt; member agents inherit the unit's bundles via
    // that layer in addition to their own Layer-4 bundles from the agent
    // store (see AgentEndpoints). The pre-#2360 string-array `Skill` surface
    // (legacy MCP-tool grant shape) was retired in this PR alongside the
    // dead IAgentStateCoordinator.{Get,Set}SkillsAsync chain.
    // -------------------------------------------------------------------------

    private static async Task<IResult> GetEquippedSkillsAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitSkillBundleStore bundleStore,
        CancellationToken cancellationToken)
    {
        var address = Address.For("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        var bundles = await bundleStore.GetAsync(actorId, cancellationToken);
        return Results.Ok(new EquippedSkillsResponse(EquippedSkillsProjection.From(bundles)));
    }

    private static async Task<IResult> EquipUnitSkillAsync(
        string id,
        EquipSkillRequest request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitSkillBundleStore bundleStore,
        CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.PackageName)
            || string.IsNullOrWhiteSpace(request.SkillName))
        {
            return Results.Problem(
                detail: "Both 'packageName' and 'skillName' are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var address = Address.For("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        try
        {
            var updated = await bundleStore.AddAsync(
                actorId,
                new SkillBundleReference(request.PackageName, request.SkillName),
                cancellationToken);
            return Results.Ok(new EquippedSkillsResponse(EquippedSkillsProjection.From(updated)));
        }
        catch (SkillBundlePackageNotFoundException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (SkillBundleNotFoundException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> UnequipUnitSkillAsync(
        string id,
        string packageName,
        string skillName,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IUnitSkillBundleStore bundleStore,
        CancellationToken cancellationToken)
    {
        var address = Address.For("unit", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        var updated = await bundleStore.RemoveAsync(actorId, packageName, skillName, cancellationToken);
        return Results.Ok(new EquippedSkillsResponse(EquippedSkillsProjection.From(updated)));
    }
}
