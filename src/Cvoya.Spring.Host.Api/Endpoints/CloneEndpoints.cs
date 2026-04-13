// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;
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
            .WithSummary("Create a clone of an agent")
            .Produces<CloneResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/", ListClonesAsync)
            .WithName("ListClones")
            .WithSummary("List clones of an agent")
            .Produces<CloneResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{cloneId}", GetCloneAsync)
            .WithName("GetClone")
            .WithSummary("Get clone status")
            .Produces<CloneResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{cloneId}", DeleteCloneAsync)
            .WithName("DeleteClone")
            .WithSummary("Delete a clone")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound);

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

        // Request DTO now carries the enums directly; no string → enum
        // mapping needed. See #183.
        var input = new CloningInput(agentId, cloneId, request.CloneType, request.AttachmentMode);

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
        IStateStore stateStore,
        CancellationToken cancellationToken)
    {
        var parentAddress = new Address("agent", agentId);
        var parentEntry = await directoryService.ResolveAsync(parentAddress, cancellationToken);

        if (parentEntry is null)
        {
            return Results.NotFound(new { Error = $"Agent '{agentId}' not found" });
        }

        var childrenKey = $"{agentId}:{StateKeys.CloneChildren}";
        var cloneIds = await stateStore.GetAsync<List<string>>(childrenKey, cancellationToken);

        if (cloneIds is null || cloneIds.Count == 0)
        {
            return Results.Ok(Array.Empty<CloneResponse>());
        }

        var clones = new List<CloneResponse>();
        foreach (var cloneId in cloneIds)
        {
            var identityKey = $"{cloneId}:{StateKeys.CloneIdentity}";
            var identity = await stateStore.GetAsync<CloneIdentity>(identityKey, cancellationToken);

            var cloneAddress = new Address("agent", cloneId);
            var cloneEntry = await directoryService.ResolveAsync(cloneAddress, cancellationToken);

            clones.Add(new CloneResponse(
                cloneId,
                agentId,
                identity?.CloningPolicy ?? CloningPolicy.EphemeralNoMemory,
                identity?.AttachmentMode ?? AttachmentMode.Detached,
                cloneEntry is not null ? "active" : "unknown",
                cloneEntry?.RegisteredAt ?? DateTimeOffset.UtcNow));
        }

        return Results.Ok(clones);
    }

    private static async Task<IResult> GetCloneAsync(
        string agentId,
        string cloneId,
        IDirectoryService directoryService,
        IStateStore stateStore,
        CancellationToken cancellationToken)
    {
        var cloneAddress = new Address("agent", cloneId);
        var cloneEntry = await directoryService.ResolveAsync(cloneAddress, cancellationToken);

        if (cloneEntry is null)
        {
            return Results.NotFound(new { Error = $"Clone '{cloneId}' not found" });
        }

        var identityKey = $"{cloneId}:{StateKeys.CloneIdentity}";
        var identity = await stateStore.GetAsync<CloneIdentity>(identityKey, cancellationToken);

        var response = new CloneResponse(
            cloneId,
            identity?.ParentAgentId ?? agentId,
            identity?.CloningPolicy ?? CloningPolicy.EphemeralNoMemory,
            identity?.AttachmentMode ?? AttachmentMode.Detached,
            "active",
            cloneEntry.RegisteredAt);

        return Results.Ok(response);
    }

    private static async Task<IResult> DeleteCloneAsync(
        string agentId,
        string cloneId,
        IDirectoryService directoryService,
        IStateStore stateStore,
        DaprWorkflowClient workflowClient,
        CancellationToken cancellationToken)
    {
        var cloneAddress = new Address("agent", cloneId);
        var cloneEntry = await directoryService.ResolveAsync(cloneAddress, cancellationToken);

        if (cloneEntry is null)
        {
            return Results.NotFound(new { Error = $"Clone '{cloneId}' not found" });
        }

        // Look up clone identity to determine cloning policy for memory flow-back.
        var identityKey = $"{cloneId}:{StateKeys.CloneIdentity}";
        var identity = await stateStore.GetAsync<CloneIdentity>(identityKey, cancellationToken);

        var cloningPolicy = identity?.CloningPolicy ?? CloningPolicy.EphemeralNoMemory;
        var attachmentMode = identity?.AttachmentMode ?? AttachmentMode.Detached;

        var input = new CloningInput(agentId, cloneId, cloningPolicy, attachmentMode);

        await workflowClient.ScheduleNewWorkflowAsync(
            nameof(CloneDestructionWorkflow),
            input: input);

        return Results.Accepted($"/api/v1/agents/{agentId}/clones/{cloneId}");
    }
}