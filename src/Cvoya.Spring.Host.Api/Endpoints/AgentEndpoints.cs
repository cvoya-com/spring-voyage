// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Models;

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
            .WithSummary("List all registered agents");

        group.MapGet("/{id}", GetAgentAsync)
            .WithName("GetAgent")
            .WithSummary("Get agent status by sending a StatusQuery message");

        group.MapPost("/", CreateAgentAsync)
            .WithName("CreateAgent")
            .WithSummary("Create a new agent");

        group.MapDelete("/{id}", DeleteAgentAsync)
            .WithName("DeleteAgent")
            .WithSummary("Delete an agent");

        return group;
    }

    private static async Task<IResult> ListAgentsAsync(
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var entries = await directoryService.ListAllAsync(cancellationToken);

        var agents = entries
            .Where(e => string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
            .Select(ToAgentResponse)
            .ToList();

        return Results.Ok(agents);
    }

    private static async Task<IResult> GetAgentAsync(
        string id,
        IDirectoryService directoryService,
        MessageRouter messageRouter,
        CancellationToken cancellationToken)
    {
        var address = new Address("agent", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.NotFound(new { Error = $"Agent '{id}' not found" });
        }

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

        if (!result.IsSuccess)
        {
            return Results.Ok(ToAgentResponse(entry));
        }

        return Results.Ok(new
        {
            Agent = ToAgentResponse(entry),
            Status = result.Value?.Payload
        });
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

    private static AgentResponse ToAgentResponse(DirectoryEntry entry) =>
        new(
            entry.ActorId,
            entry.Address.Path,
            entry.DisplayName,
            entry.Description,
            entry.Role,
            entry.RegisteredAt);
}
