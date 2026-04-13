// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Mvc;

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
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}", DeleteAgentAsync)
            .WithName("DeleteAgent")
            .WithSummary("Delete an agent")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

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
        [FromServices] MessageRouter messageRouter,
        CancellationToken cancellationToken)
    {
        var address = new Address("agent", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Agent '{id}' not found" });
        }

        var metadata = await TryGetAgentMetadataAsync(actorProxyFactory, entry.ActorId, cancellationToken);

        // Send a StatusQuery message to the agent.
        var statusQuery = new Message(
            Guid.NewGuid(),
            new Address("human", "api"),
            address,
            MessageType.StatusQuery,
            null,
            default,
            DateTimeOffset.UtcNow);

        var result = await messageRouter.RouteAsync(statusQuery, cancellationToken);

        var agentResponse = ToAgentResponse(entry, metadata);
        if (!result.IsSuccess)
        {
            return Results.Ok(new AgentDetailResponse(agentResponse, null));
        }

        return Results.Ok(new AgentDetailResponse(agentResponse, result.Value?.Payload));
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
            return Results.NotFound(new { Error = $"Agent '{id}' not found" });
        }

        // ParentUnit is intentionally not accepted here — changing containment
        // must go through the unit's assign / unassign endpoints so the
        // agent.ParentUnit ↔ unit.Members invariant stays consistent.
        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(entry.ActorId), nameof(IAgentActor));

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
            return Results.NotFound(new { Error = $"Agent '{id}' not found" });
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
            return Results.NotFound(new { Error = $"Agent '{id}' not found" });
        }

        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(entry.ActorId), nameof(IAgentActor));

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
            return Results.BadRequest(new { Error = "Skills list is required (use [] to clear)." });
        }

        var entry = await directoryService.ResolveAsync(new Address("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Agent '{id}' not found" });
        }

        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(entry.ActorId), nameof(IAgentActor));

        await proxy.SetSkillsAsync(request.Skills, cancellationToken);

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
                new ActorId(actorId), nameof(IAgentActor));
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
}