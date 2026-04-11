// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Workflow;

/// <summary>
/// Maps clone-related API endpoints for creating, listing, retrieving, and deleting agent clones.
/// </summary>
public static class CloneEndpoints
{
    /// <summary>
    /// Registers clone endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapCloneEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/agents/{agentId}/clones")
            .WithTags("Clones");

        group.MapPost("/", CreateCloneAsync)
            .WithName("CreateClone")
            .WithSummary("Create a clone of an agent");

        group.MapGet("/", ListClonesAsync)
            .WithName("ListClones")
            .WithSummary("List clones of an agent");

        group.MapGet("/{cloneId}", GetCloneAsync)
            .WithName("GetClone")
            .WithSummary("Get clone status");

        group.MapDelete("/{cloneId}", DeleteCloneAsync)
            .WithName("DeleteClone")
            .WithSummary("Delete a clone");

        return group;
    }

    private static async Task<IResult> CreateCloneAsync(
        string agentId,
        CreateCloneRequest request,
        IDirectoryService directoryService,
        DaprWorkflowClient workflowClient,
        CancellationToken cancellationToken)
    {
        var parentAddress = new Address("agent", agentId);
        var parentEntry = await directoryService.ResolveAsync(parentAddress, cancellationToken);

        if (parentEntry is null)
        {
            return Results.NotFound(new { Error = $"Agent '{agentId}' not found" });
        }

        var cloneId = Guid.NewGuid().ToString();

        var cloningPolicy = request.CloneType switch
        {
            "ephemeral-with-memory" => CloningPolicy.EphemeralWithMemory,
            "ephemeral-no-memory" => CloningPolicy.EphemeralNoMemory,
            _ => CloningPolicy.EphemeralNoMemory
        };

        var attachmentMode = request.AttachmentMode switch
        {
            "attached" => AttachmentMode.Attached,
            _ => AttachmentMode.Detached
        };

        var input = new CloningInput(agentId, cloneId, cloningPolicy, attachmentMode);

        await workflowClient.ScheduleNewWorkflowAsync(
            nameof(CloningLifecycleWorkflow),
            input: input);

        var response = new CloneResponse(
            cloneId,
            agentId,
            request.CloneType,
            request.AttachmentMode,
            "provisioning",
            DateTimeOffset.UtcNow);

        return Results.Accepted($"/api/v1/agents/{agentId}/clones/{cloneId}", response);
    }

    private static async Task<IResult> ListClonesAsync(
        string agentId,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var parentAddress = new Address("agent", agentId);
        var parentEntry = await directoryService.ResolveAsync(parentAddress, cancellationToken);

        if (parentEntry is null)
        {
            return Results.NotFound(new { Error = $"Agent '{agentId}' not found" });
        }

        // Full implementation depends on directory tracking clones; return empty list for now.
        return Results.Ok(Array.Empty<CloneResponse>());
    }

    private static async Task<IResult> GetCloneAsync(
        string agentId,
        string cloneId,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var cloneAddress = new Address("agent", cloneId);
        var cloneEntry = await directoryService.ResolveAsync(cloneAddress, cancellationToken);

        if (cloneEntry is null)
        {
            return Results.NotFound(new { Error = $"Clone '{cloneId}' not found" });
        }

        var response = new CloneResponse(
            cloneId,
            agentId,
            "ephemeral-no-memory",
            "detached",
            "active",
            cloneEntry.RegisteredAt);

        return Results.Ok(response);
    }

    private static async Task<IResult> DeleteCloneAsync(
        string agentId,
        string cloneId,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var cloneAddress = new Address("agent", cloneId);
        var cloneEntry = await directoryService.ResolveAsync(cloneAddress, cancellationToken);

        if (cloneEntry is null)
        {
            return Results.NotFound(new { Error = $"Clone '{cloneId}' not found" });
        }

        await directoryService.UnregisterAsync(cloneAddress, cancellationToken);

        return Results.NoContent();
    }
}