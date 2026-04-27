// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps initiative-related API endpoints for reading and updating per-agent and
/// per-unit <see cref="InitiativePolicy"/> values, and reading an agent's current
/// effective <see cref="InitiativeLevel"/>.
/// </summary>
public static class InitiativeEndpoints
{
    /// <summary>
    /// Registers initiative endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapInitiativeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant")
            .WithTags("Initiative");

        group.MapGet("/agents/{id}/initiative/policy", GetAgentInitiativePolicyAsync)
            .WithName("GetAgentInitiativePolicy")
            .WithSummary("Get the initiative policy for an agent")
            .Produces<InitiativePolicy>(StatusCodes.Status200OK);

        group.MapPut("/agents/{id}/initiative/policy", SetAgentInitiativePolicyAsync)
            .WithName("SetAgentInitiativePolicy")
            .WithSummary("Set the initiative policy for an agent")
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/units/{id}/initiative/policy", GetUnitInitiativePolicyAsync)
            .WithName("GetUnitInitiativePolicy")
            .WithSummary("Get the initiative policy for a unit")
            .Produces<InitiativePolicy>(StatusCodes.Status200OK);

        group.MapPut("/units/{id}/initiative/policy", SetUnitInitiativePolicyAsync)
            .WithName("SetUnitInitiativePolicy")
            .WithSummary("Set the initiative policy for a unit")
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/agents/{id}/initiative/level", GetAgentInitiativeLevelAsync)
            .WithName("GetAgentInitiativeLevel")
            .WithSummary("Get the current effective initiative level for an agent")
            .Produces<InitiativeLevelResponse>(StatusCodes.Status200OK);

        return group;
    }

    private static async Task<IResult> GetAgentInitiativePolicyAsync(
        string id,
        [FromServices] IAgentPolicyStore policyStore,
        CancellationToken cancellationToken)
    {
        var policy = await policyStore.GetPolicyAsync($"agent:{id}", cancellationToken);
        return Results.Ok(policy);
    }

    private static async Task<IResult> SetAgentInitiativePolicyAsync(
        string id,
        InitiativePolicy policy,
        [FromServices] IAgentPolicyStore policyStore,
        CancellationToken cancellationToken)
    {
        await policyStore.SetPolicyAsync($"agent:{id}", policy, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetUnitInitiativePolicyAsync(
        string id,
        [FromServices] IAgentPolicyStore policyStore,
        CancellationToken cancellationToken)
    {
        var policy = await policyStore.GetPolicyAsync($"unit:{id}", cancellationToken);
        return Results.Ok(policy);
    }

    private static async Task<IResult> SetUnitInitiativePolicyAsync(
        string id,
        InitiativePolicy policy,
        [FromServices] IAgentPolicyStore policyStore,
        CancellationToken cancellationToken)
    {
        await policyStore.SetPolicyAsync($"unit:{id}", policy, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetAgentInitiativeLevelAsync(
        string id,
        [FromServices] IInitiativeEngine initiativeEngine,
        CancellationToken cancellationToken)
    {
        var level = await initiativeEngine.GetCurrentLevelAsync(id, cancellationToken);
        return Results.Ok(new InitiativeLevelResponse(level));
    }
}