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

            var cloneType = identity?.CloningPolicy switch
            {
                CloningPolicy.EphemeralWithMemory => "ephemeral-with-memory",
                CloningPolicy.EphemeralNoMemory => "ephemeral-no-memory",
                _ => "ephemeral-no-memory"
            };

            var attachmentMode = identity?.AttachmentMode switch
            {
                AttachmentMode.Attached => "attached",
                _ => "detached"
            };

            clones.Add(new CloneResponse(
                cloneId,
                agentId,
                cloneType,
                attachmentMode,
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

        var cloneType = identity?.CloningPolicy switch
        {
            CloningPolicy.EphemeralWithMemory => "ephemeral-with-memory",
            CloningPolicy.EphemeralNoMemory => "ephemeral-no-memory",
            _ => "ephemeral-no-memory"
        };

        var attachmentMode = identity?.AttachmentMode switch
        {
            AttachmentMode.Attached => "attached",
            _ => "detached"
        };

        var response = new CloneResponse(
            cloneId,
            identity?.ParentAgentId ?? agentId,
            cloneType,
            attachmentMode,
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