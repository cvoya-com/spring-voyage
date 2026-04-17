// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Maps agent-related API endpoints.
/// </summary>
public static class AgentEndpoints
{
    /// <summary>
    /// Registers agent endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/agents")
            .WithTags("Agents");

        group.MapGet("/", ListAgentsAsync)
            .WithName("ListAgents")
            .WithSummary("List all registered agents")
            .Produces<AgentResponse[]>(StatusCodes.Status200OK);

        group.MapGet("/{id}", GetAgentAsync)
            .WithName("GetAgent")
            .WithSummary("Get agent status by sending a StatusQuery message")
            .Produces<AgentDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateAgentAsync)
            .WithName("CreateAgent")
            .WithSummary("Create a new agent")
            .Produces<AgentResponse>(StatusCodes.Status201Created);

        group.MapPatch("/{id}", UpdateAgentMetadataAsync)
            .WithName("UpdateAgentMetadata")
            .WithSummary("Update the agent's metadata (model, specialty, enabled, execution mode)")
            .Produces<AgentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/skills", GetAgentSkillsAsync)
            .WithName("GetAgentSkills")
            .WithSummary("Get the agent's configured skill list (tool names the agent is allowed to invoke)")
            .Produces<AgentSkillsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id}/skills", SetAgentSkillsAsync)
            .WithName("SetAgentSkills")
            .WithSummary("Replace the agent's skill list in full; empty list means the agent is disabled from every tool")
            .Produces<AgentSkillsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}", DeleteAgentAsync)
            .WithName("DeleteAgent")
            .WithSummary("Delete an agent")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Persistent-agent lifecycle surface (#396). Distinct from ephemeral
        // dispatch — `deploy` stands up the long-lived container, `undeploy`
        // tears it down, `scale` changes replica count (reserved), and `logs`
        // streams the container tail. `delete` above still removes the agent
        // record itself; a persistent agent should be undeployed first so the
        // dangling container is cleaned up.
        group.MapPost("/{id}/deploy", DeployPersistentAgentAsync)
            .WithName("DeployPersistentAgent")
            .WithSummary("Deploy (or reconcile) a persistent agent's backing container")
            .Produces<PersistentAgentDeploymentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/undeploy", UndeployPersistentAgentAsync)
            .WithName("UndeployPersistentAgent")
            .WithSummary("Tear down a persistent agent's backing container (idempotent)")
            .Produces<PersistentAgentDeploymentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/scale", ScalePersistentAgentAsync)
            .WithName("ScalePersistentAgent")
            .WithSummary("Adjust replica count for a persistent agent (OSS core supports 0 or 1 today)")
            .Produces<PersistentAgentDeploymentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/logs", GetPersistentAgentLogsAsync)
            .WithName("GetPersistentAgentLogs")
            .WithSummary("Read the container logs for a persistent agent")
            .Produces<PersistentAgentLogsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/deployment", GetPersistentAgentDeploymentAsync)
            .WithName("GetPersistentAgentDeployment")
            .WithSummary("Get the current deployment state of a persistent agent (container + health)")
            .Produces<PersistentAgentDeploymentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> DeployPersistentAgentAsync(
        string id,
        DeployPersistentAgentRequest? request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] PersistentAgentLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // The OSS core only supports replicas ∈ {0, 1} today. We accept a
        // nullable int on the wire so the default (and most callers) don't
        // need to send a body at all.
        var replicas = request?.Replicas ?? 1;
        if (replicas < 0 || replicas > 1)
        {
            return Results.Problem(
                detail: "Only replicas in {0, 1} are supported by the OSS core; horizontal scaling is a tracked follow-up.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            if (replicas == 0)
            {
                // Scale-to-zero intent: the caller asked to deploy with 0
                // replicas. Treat as undeploy and return the canonical empty
                // shape so CLIs see a consistent wire contract.
                await lifecycle.UndeployAsync(id, cancellationToken);
                return Results.Ok(EmptyDeploymentResponse(id, replicas: 0));
            }

            var deployed = await lifecycle.DeployAsync(
                id,
                imageOverride: request?.Image,
                cancellationToken);
            return Results.Ok(ToDeploymentResponse(deployed, replicas: 1));
        }
        catch (SpringException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> UndeployPersistentAgentAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] PersistentAgentLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        await lifecycle.UndeployAsync(id, cancellationToken);

        // Always return the canonical "not running" shape so the CLI can
        // treat the response the same whether the agent was running or not.
        return Results.Ok(EmptyDeploymentResponse(id, replicas: 0));
    }

    private static async Task<IResult> ScalePersistentAgentAsync(
        string id,
        ScalePersistentAgentRequest request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] PersistentAgentLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            var scaled = await lifecycle.ScaleAsync(id, request.Replicas, cancellationToken);
            return Results.Ok(ToDeploymentResponse(scaled, request.Replicas));
        }
        catch (SpringException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> GetPersistentAgentLogsAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] PersistentAgentLifecycle lifecycle,
        [FromServices] PersistentAgentRegistry registry,
        [FromQuery] int? tail,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var effectiveTail = tail is > 0 ? tail.Value : 200;

        try
        {
            var logs = await lifecycle.GetLogsAsync(id, effectiveTail, cancellationToken);
            registry.TryGet(id, out var registered);
            return Results.Ok(new PersistentAgentLogsResponse(
                AgentId: id,
                ContainerId: registered?.ContainerId ?? string.Empty,
                Tail: effectiveTail,
                Logs: logs));
        }
        catch (SpringException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
    }

    private static async Task<IResult> GetPersistentAgentDeploymentAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] PersistentAgentRegistry registry,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        if (registry.TryGet(id, out var deployment) && deployment is not null)
        {
            return Results.Ok(ToDeploymentResponse(deployment, replicas: 1));
        }

        return Results.Ok(EmptyDeploymentResponse(id, replicas: 0));
    }

    /// <summary>
    /// Canonical "running" wire shape for a persistent deployment. Maps the
    /// registry's <see cref="PersistentAgentEntry"/> to the HTTP response so
    /// callers never have to reach into the registry types directly.
    /// </summary>
    internal static PersistentAgentDeploymentResponse ToDeploymentResponse(
        PersistentAgentEntry entry,
        int replicas) =>
        new(
            AgentId: entry.AgentId,
            Running: entry.ContainerId is not null,
            HealthStatus: entry.HealthStatus switch
            {
                AgentHealthStatus.Healthy => "healthy",
                AgentHealthStatus.Unhealthy => "unhealthy",
                _ => "unknown",
            },
            Replicas: replicas,
            Image: entry.Definition?.Execution?.Image,
            Endpoint: entry.Endpoint?.ToString(),
            ContainerId: entry.ContainerId,
            StartedAt: entry.StartedAt,
            ConsecutiveFailures: entry.ConsecutiveFailures);

    /// <summary>
    /// Canonical "not running" shape. Returned when there is no entry in the
    /// registry (never deployed, or already undeployed).
    /// </summary>
    internal static PersistentAgentDeploymentResponse EmptyDeploymentResponse(string agentId, int replicas) =>
        new(
            AgentId: agentId,
            Running: false,
            HealthStatus: "unknown",
            Replicas: replicas,
            Image: null,
            Endpoint: null,
            ContainerId: null,
            StartedAt: null,
            ConsecutiveFailures: 0);

    private static async Task<IResult> ListAgentsAsync(
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var entries = await directoryService.ListAllAsync(cancellationToken);

        // Intentionally does NOT enrich with actor metadata — the list
        // endpoint is a cheap directory scan and callers who need per-agent
        // metadata use GET /api/v1/agents/{id} or the unit-scoped list.
        // Response fields below RegisteredAt fall back to defaults (see
        // ToAgentResponse).
        var agents = entries
            .Where(e => string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
            .Select(e => ToAgentResponse(e))
            .ToList();

        return Results.Ok(agents);
    }

    private static async Task<IResult> GetAgentAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromServices] MessageRouter messageRouter,
        [FromServices] IAuthenticatedCallerAccessor callerAccessor,
        [FromServices] PersistentAgentRegistry persistentAgentRegistry,
        CancellationToken cancellationToken)
    {
        var address = new Address("agent", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(entry.ActorId), nameof(AgentActor));
        var metadata = await GetDerivedAgentMetadataAsync(proxy, membershipRepository, id, cancellationToken);

        // #339: Thread the authenticated caller's identity through as the
        // From address rather than hardcoding `human://api`. The router's
        // permission gate only fires for `unit://` destinations today, so
        // `agent://` dispatch works either way — but the synthetic identity
        // dropped observability (activity events are labelled with the
        // sender) and masked auth bugs. Falls back to `human://api` only
        // when no authenticated principal is present.
        var statusQuery = new Message(
            Guid.NewGuid(),
            callerAccessor.GetHumanAddress(),
            address,
            MessageType.StatusQuery,
            null,
            default,
            DateTimeOffset.UtcNow);

        var result = await messageRouter.RouteAsync(statusQuery, cancellationToken);

        // Persistent-agent health enrichment (#396): when a persistent
        // deployment is tracked, surface it alongside the actor's status
        // payload so `spring agent status <id>` is a single stop for both
        // ephemeral-actor state and persistent-container state.
        PersistentAgentDeploymentResponse? deployment = null;
        if (persistentAgentRegistry.TryGet(id, out var persistentEntry) && persistentEntry is not null)
        {
            deployment = ToDeploymentResponse(persistentEntry, replicas: 1);
        }

        var agentResponse = ToAgentResponse(entry, metadata);
        if (!result.IsSuccess)
        {
            return Results.Ok(new AgentDetailResponse(agentResponse, null, deployment));
        }

        return Results.Ok(new AgentDetailResponse(agentResponse, result.Value?.Payload, deployment));
    }

    private static async Task<IResult> UpdateAgentMetadataAsync(
        string id,
        UpdateAgentMetadataRequest request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var address = new Address("agent", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // ParentUnit is intentionally not accepted here — changing containment
        // must go through the unit's assign / unassign endpoints so the
        // agent.ParentUnit ↔ unit.Members invariant stays consistent.
        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(entry.ActorId), nameof(AgentActor));

        await proxy.SetMetadataAsync(
            new AgentMetadata(
                Model: request.Model,
                Specialty: request.Specialty,
                Enabled: request.Enabled,
                ExecutionMode: request.ExecutionMode,
                ParentUnit: null),
            cancellationToken);

        var updated = await proxy.GetMetadataAsync(cancellationToken);
        return Results.Ok(ToAgentResponse(entry, updated));
    }

    private static async Task<IResult> CreateAgentAsync(
        CreateAgentRequest request,
        IDirectoryService directoryService,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var actorId = Guid.NewGuid().ToString();
        var address = new Address("agent", request.Name);
        var entry = new DirectoryEntry(
            address,
            actorId,
            request.DisplayName,
            request.Description,
            request.Role,
            DateTimeOffset.UtcNow);

        await directoryService.RegisterAsync(entry, cancellationToken);

        // If the caller supplied a definition JSON document, parse and persist
        // it on the AgentDefinitionEntity so IAgentDefinitionProvider can
        // surface the execution configuration to the dispatcher. This is the
        // YAML-only path for selecting the agent's runtime (tool / image /
        // provider / model) — required by #480 acceptance so switching provider
        // is a pure-configuration change, no code edit.
        if (!string.IsNullOrWhiteSpace(request.DefinitionJson))
        {
            JsonElement definition;
            try
            {
                using var doc = JsonDocument.Parse(request.DefinitionJson);
                definition = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return Results.Problem(
                    detail: $"DefinitionJson is not valid JSON: {ex.Message}",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var entity = await db.AgentDefinitions
                .FirstOrDefaultAsync(a => a.AgentId == request.Name, cancellationToken);
            if (entity is not null)
            {
                entity.Definition = definition;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return Results.Created($"/api/v1/agents/{request.Name}", ToAgentResponse(entry));
    }

    private static async Task<IResult> DeleteAgentAsync(
        string id,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var address = new Address("agent", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        await directoryService.UnregisterAsync(address, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> GetAgentSkillsAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(entry.ActorId), nameof(AgentActor));

        var skills = await proxy.GetSkillsAsync(cancellationToken);
        return Results.Ok(new AgentSkillsResponse(skills));
    }

    private static async Task<IResult> SetAgentSkillsAsync(
        string id,
        SetAgentSkillsRequest request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        if (request.Skills is null)
        {
            return Results.Problem(detail: "Skills list is required (use [] to clear).", statusCode: StatusCodes.Status400BadRequest);
        }

        var entry = await directoryService.ResolveAsync(new Address("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(entry.ActorId), nameof(AgentActor));

        await proxy.SetSkillsAsync(request.Skills.ToArray(), cancellationToken);

        var updated = await proxy.GetSkillsAsync(cancellationToken);
        return Results.Ok(new AgentSkillsResponse(updated));
    }

    /// <summary>
    /// Projects a directory entry (+ optional actor-owned metadata) into the
    /// wire shape. When <paramref name="metadata"/> is <c>null</c> the
    /// response carries default values (<c>Enabled = true</c>, <c>ExecutionMode = Auto</c>)
    /// so callers can treat those fields as non-nullable.
    /// </summary>
    internal static AgentResponse ToAgentResponse(
        DirectoryEntry entry,
        AgentMetadata? metadata = null) =>
        new(
            entry.ActorId,
            entry.Address.Path,
            entry.DisplayName,
            entry.Description,
            entry.Role,
            entry.RegisteredAt,
            metadata?.Model,
            metadata?.Specialty,
            metadata?.Enabled ?? true,
            metadata?.ExecutionMode ?? AgentExecutionMode.Auto,
            metadata?.ParentUnit);

    /// <summary>
    /// Best-effort read of the agent actor's metadata. A failure here is
    /// non-fatal — callers fall back to the wire defaults (see
    /// <see cref="ToAgentResponse(DirectoryEntry, AgentMetadata?)"/>) so a
    /// transient actor outage doesn't blank the agent from the directory.
    /// The failure is logged by the caller via <paramref name="logger"/>.
    /// </summary>
    internal static async Task<AgentMetadata?> TryGetAgentMetadataAsync(
        IActorProxyFactory actorProxyFactory,
        string actorId,
        CancellationToken cancellationToken,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
                new ActorId(actorId), nameof(AgentActor));
            return await proxy.GetMetadataAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Failed to read metadata for agent actor {ActorId}; falling back to defaults.",
                actorId);
            return null;
        }
    }

    /// <summary>
    /// Reads the agent's actor-owned metadata and overrides
    /// <see cref="AgentMetadata.ParentUnit"/> with a server-side derivation
    /// from the membership table. The derivation rule is "first by
    /// <c>CreatedAt</c>" — C2b-1 leaves it simple; a future
    /// <c>IsPrimary</c> flag may refine this without a wire-shape change.
    /// See #160: the membership table is authoritative; the cached
    /// <c>Agent:ParentUnit</c> state on the actor is a legacy mirror kept
    /// for non-critical readers and the backfill path.
    /// </summary>
    internal static async Task<AgentMetadata?> GetDerivedAgentMetadataAsync(
        IAgentActor proxy,
        IUnitMembershipRepository membershipRepository,
        string agentAddress,
        CancellationToken cancellationToken)
    {
        AgentMetadata? metadata = null;
        try
        {
            metadata = await proxy.GetMetadataAsync(cancellationToken);
        }
        catch
        {
            // Falls through to the membership-driven projection below.
        }

        var memberships = await membershipRepository.ListByAgentAsync(agentAddress, cancellationToken);
        var derivedParent = memberships.Count > 0 ? memberships[0].UnitId : null;

        if (metadata is null)
        {
            // No actor state; synthesise a metadata record so the response
            // still carries the derived parent (and defaults for the rest).
            return new AgentMetadata(ParentUnit: derivedParent);
        }

        return metadata with { ParentUnit = derivedParent };
    }
}