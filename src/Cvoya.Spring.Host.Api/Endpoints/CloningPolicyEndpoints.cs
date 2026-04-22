// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Persistent cloning-policy endpoints (#416). Two surfaces, same
/// wire shape: per-agent
/// (<c>/api/v1/agents/{id}/cloning-policy</c>) and tenant-wide
/// (<c>/api/v1/tenant/cloning-policy</c>). A target that has never had
/// a policy persisted returns <see cref="AgentCloningPolicy.Empty"/> —
/// callers never need to branch on 404 vs empty-policy.
/// </summary>
public static class CloningPolicyEndpoints
{
    /// <summary>
    /// Registers the cloning-policy endpoints on the supplied route builder.
    /// Call sites can chain <c>.RequireAuthorization()</c> on either the
    /// per-agent or tenant-wide groups separately; this method registers
    /// both and returns the per-agent group — the tenant group is attached
    /// internally and sits behind the same auth policy the caller applies
    /// to the overall mount.
    /// </summary>
    public static IEndpointRouteBuilder MapCloningPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/api/v1/agents/{id}/cloning-policy")
            .WithTags("CloningPolicy")
            .RequireAuthorization();

        agent.MapGet("/", GetAgentCloningPolicyAsync)
            .WithName("GetAgentCloningPolicy")
            .WithSummary("Get the persistent cloning policy for an agent")
            .Produces<AgentCloningPolicyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        agent.MapPut("/", SetAgentCloningPolicyAsync)
            .WithName("SetAgentCloningPolicy")
            .WithSummary("Upsert the persistent cloning policy for an agent")
            .Produces<AgentCloningPolicyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        agent.MapDelete("/", DeleteAgentCloningPolicyAsync)
            .WithName("DeleteAgentCloningPolicy")
            .WithSummary("Clear the persistent cloning policy for an agent")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var tenant = app.MapGroup("/api/v1/tenant/cloning-policy")
            .WithTags("CloningPolicy")
            .RequireAuthorization();

        tenant.MapGet("/", GetTenantCloningPolicyAsync)
            .WithName("GetTenantCloningPolicy")
            .WithSummary("Get the tenant-wide persistent cloning policy")
            .Produces<AgentCloningPolicyResponse>(StatusCodes.Status200OK);

        tenant.MapPut("/", SetTenantCloningPolicyAsync)
            .WithName("SetTenantCloningPolicy")
            .WithSummary("Upsert the tenant-wide persistent cloning policy")
            .Produces<AgentCloningPolicyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        tenant.MapDelete("/", DeleteTenantCloningPolicyAsync)
            .WithName("DeleteTenantCloningPolicy")
            .WithSummary("Clear the tenant-wide persistent cloning policy")
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }

    private static async Task<IResult> GetAgentCloningPolicyAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IAgentCloningPolicyRepository repository,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Agent '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var policy = await repository.GetAsync(CloningPolicyScope.Agent, id, cancellationToken);
        return Results.Ok(AgentCloningPolicyResponse.From(policy));
    }

    private static async Task<IResult> SetAgentCloningPolicyAsync(
        string id,
        AgentCloningPolicyResponse request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IAgentCloningPolicyRepository repository,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Agent '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var policy = request.ToCore();
        await repository.SetAsync(CloningPolicyScope.Agent, id, policy, cancellationToken);
        return Results.Ok(AgentCloningPolicyResponse.From(policy));
    }

    private static async Task<IResult> DeleteAgentCloningPolicyAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IAgentCloningPolicyRepository repository,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Agent '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        await repository.DeleteAsync(CloningPolicyScope.Agent, id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetTenantCloningPolicyAsync(
        [FromServices] ITenantContext tenantContext,
        [FromServices] IAgentCloningPolicyRepository repository,
        CancellationToken cancellationToken)
    {
        var policy = await repository.GetAsync(
            CloningPolicyScope.Tenant, tenantContext.CurrentTenantId, cancellationToken);
        return Results.Ok(AgentCloningPolicyResponse.From(policy));
    }

    private static async Task<IResult> SetTenantCloningPolicyAsync(
        AgentCloningPolicyResponse request,
        [FromServices] ITenantContext tenantContext,
        [FromServices] IAgentCloningPolicyRepository repository,
        CancellationToken cancellationToken)
    {
        var policy = request.ToCore();
        await repository.SetAsync(
            CloningPolicyScope.Tenant, tenantContext.CurrentTenantId, policy, cancellationToken);
        return Results.Ok(AgentCloningPolicyResponse.From(policy));
    }

    private static async Task<IResult> DeleteTenantCloningPolicyAsync(
        [FromServices] ITenantContext tenantContext,
        [FromServices] IAgentCloningPolicyRepository repository,
        CancellationToken cancellationToken)
    {
        await repository.DeleteAsync(
            CloningPolicyScope.Tenant, tenantContext.CurrentTenantId, cancellationToken);
        return Results.NoContent();
    }
}