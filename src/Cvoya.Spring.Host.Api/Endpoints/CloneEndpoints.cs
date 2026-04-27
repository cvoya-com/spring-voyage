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
        var group = app.MapGroup("/api/v1/tenant/agents/{agentId}/clones")
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
        IAgentCloningPolicyEnforcer policyEnforcer,
        DaprWorkflowClient workflowClient,
        CancellationToken cancellationToken)
    {
        var parentAddress = new Address("agent", agentId);
        var parentEntry = await directoryService.ResolveAsync(parentAddress, cancellationToken);

        if (parentEntry is null)
        {
            return Results.Problem(detail: $"Agent '{agentId}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // Persistent cloning policy (#416). The enforcer walks agent-scoped
        // then tenant-scoped policies and also cross-references the source
        // agent's unit boundaries (PR #497) so an Opaque wall can't be
        // bypassed by spawning a detached peer. When denied, we surface a
        // 403 with the dimension name and reason so CLI/portal callers can
        // explain exactly which rule fired. Resolved caps are forwarded
        // into the workflow so ValidateCloneRequestActivity enforces them
        // alongside the existing per-request knobs.
        var decision = await policyEnforcer.EvaluateAsync(
            agentId, request.CloneType, request.AttachmentMode, cancellationToken);

        if (!decision.Allowed)
        {
            return Results.Problem(
                title: "Cloning policy denied the request",
                detail: decision.Reason,
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?>
                {
                    ["deniedDimension"] = decision.DeniedDimension,
                });
        }

        var cloneId = Guid.NewGuid().ToString();

        // Request DTO now carries the enums directly; no string → enum
        // mapping needed. See #183. MaxClones / Budget resolved by the
        // policy enforcer flow into the workflow so the existing
        // ValidateCloneRequestActivity machinery enforces them without a
        // second code path. Request-inline knobs are absent from
        // CreateCloneRequest today, so the policy values win outright.
        var input = new CloningInput(
            agentId,
            cloneId,
            request.CloneType,
            request.AttachmentMode,
            Budget: decision.ResolvedBudget,
            MaxClones: decision.ResolvedMaxClones);

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

        return Results.Accepted($"/api/v1/tenant/agents/{agentId}/clones/{cloneId}", response);
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
            return Results.Problem(detail: $"Agent '{agentId}' not found", statusCode: StatusCodes.Status404NotFound);
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
            return Results.Problem(detail: $"Clone '{cloneId}' not found", statusCode: StatusCodes.Status404NotFound);
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
            return Results.Problem(detail: $"Clone '{cloneId}' not found", statusCode: StatusCodes.Status404NotFound);
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

        return Results.Accepted($"/api/v1/tenant/agents/{agentId}/clones/{cloneId}");
    }
}