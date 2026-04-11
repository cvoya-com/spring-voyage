// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Maps budget management API endpoints for agents and tenants.
/// </summary>
public static class BudgetEndpoints
{
    /// <summary>
    /// Registers budget endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapBudgetEndpoints(this IEndpointRouteBuilder app)
    {
        var agentGroup = app.MapGroup("/api/v1/agents/{agentId}/budget")
            .WithTags("Budgets");

        agentGroup.MapGet("/", GetAgentBudgetAsync)
            .WithName("GetAgentBudget")
            .WithSummary("Get the cost budget for an agent");

        agentGroup.MapPut("/", SetAgentBudgetAsync)
            .WithName("SetAgentBudget")
            .WithSummary("Set the cost budget for an agent");

        var tenantGroup = app.MapGroup("/api/v1/tenant/budget")
            .WithTags("Budgets");

        tenantGroup.MapGet("/", GetTenantBudgetAsync)
            .WithName("GetTenantBudget")
            .WithSummary("Get the cost budget for the tenant");

        tenantGroup.MapPut("/", SetTenantBudgetAsync)
            .WithName("SetTenantBudget")
            .WithSummary("Set the cost budget for the tenant");

        return agentGroup;
    }

    private static async Task<IResult> GetAgentBudgetAsync(
        string agentId,
        IStateStore stateStore,
        CancellationToken cancellationToken)
    {
        var key = $"{agentId}:{StateKeys.AgentCostBudget}";
        var budget = await stateStore.GetAsync<decimal?>(key, cancellationToken);

        if (budget is null)
        {
            return Results.NotFound(new { Error = $"No budget set for agent '{agentId}'" });
        }

        return Results.Ok(new BudgetResponse(budget.Value));
    }

    private static async Task<IResult> SetAgentBudgetAsync(
        string agentId,
        SetBudgetRequest request,
        IStateStore stateStore,
        CancellationToken cancellationToken)
    {
        if (request.DailyBudget <= 0)
        {
            return Results.BadRequest(new { Error = "DailyBudget must be greater than zero" });
        }

        var key = $"{agentId}:{StateKeys.AgentCostBudget}";
        await stateStore.SetAsync(key, request.DailyBudget, cancellationToken);

        return Results.Ok(new BudgetResponse(request.DailyBudget));
    }

    private static async Task<IResult> GetTenantBudgetAsync(
        IStateStore stateStore,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var tenant = tenantId ?? "default";
        var key = $"{tenant}:{StateKeys.TenantCostBudget}";
        var budget = await stateStore.GetAsync<decimal?>(key, cancellationToken);

        if (budget is null)
        {
            return Results.NotFound(new { Error = $"No budget set for tenant '{tenant}'" });
        }

        return Results.Ok(new BudgetResponse(budget.Value));
    }

    private static async Task<IResult> SetTenantBudgetAsync(
        SetBudgetRequest request,
        IStateStore stateStore,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (request.DailyBudget <= 0)
        {
            return Results.BadRequest(new { Error = "DailyBudget must be greater than zero" });
        }

        var tenant = tenantId ?? "default";
        var key = $"{tenant}:{StateKeys.TenantCostBudget}";
        await stateStore.SetAsync(key, request.DailyBudget, cancellationToken);

        return Results.Ok(new BudgetResponse(request.DailyBudget));
    }
}