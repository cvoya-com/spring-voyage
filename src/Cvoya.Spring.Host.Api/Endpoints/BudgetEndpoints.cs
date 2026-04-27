// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Maps budget management API endpoints for agents, units, and tenants.
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
        var agentGroup = app.MapGroup("/api/v1/tenant/agents/{agentId}/budget")
            .WithTags("Budgets");

        agentGroup.MapGet("/", GetAgentBudgetAsync)
            .WithName("GetAgentBudget")
            .WithSummary("Get the cost budget for an agent")
            .Produces<BudgetResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        agentGroup.MapPut("/", SetAgentBudgetAsync)
            .WithName("SetAgentBudget")
            .WithSummary("Set the cost budget for an agent")
            .Produces<BudgetResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        var tenantGroup = app.MapGroup("/api/v1/tenant/budget")
            .WithTags("Budgets");

        tenantGroup.MapGet("/", GetTenantBudgetAsync)
            .WithName("GetTenantBudget")
            .WithSummary("Get the cost budget for the tenant")
            .Produces<BudgetResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        tenantGroup.MapPut("/", SetTenantBudgetAsync)
            .WithName("SetTenantBudget")
            .WithSummary("Set the cost budget for the tenant")
            .Produces<BudgetResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // Unit-scoped budget (PR-C3 / #459). Mirrors the agent surface so the
        // CLI's `spring cost set-budget --scope unit` and the portal's
        // per-unit "Edit budget" action target the same endpoint.
        var unitGroup = app.MapGroup("/api/v1/tenant/units/{unitId}/budget")
            .WithTags("Budgets");

        unitGroup.MapGet("/", GetUnitBudgetAsync)
            .WithName("GetUnitBudget")
            .WithSummary("Get the cost budget for a unit")
            .Produces<BudgetResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        unitGroup.MapPut("/", SetUnitBudgetAsync)
            .WithName("SetUnitBudget")
            .WithSummary("Set the cost budget for a unit")
            .Produces<BudgetResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

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
            return Results.Problem(detail: $"No budget set for agent '{agentId}'", statusCode: StatusCodes.Status404NotFound);
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
            return Results.Problem(detail: "DailyBudget must be greater than zero", statusCode: StatusCodes.Status400BadRequest);
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
            return Results.Problem(detail: $"No budget set for tenant '{tenant}'", statusCode: StatusCodes.Status404NotFound);
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
            return Results.Problem(detail: "DailyBudget must be greater than zero", statusCode: StatusCodes.Status400BadRequest);
        }

        var tenant = tenantId ?? "default";
        var key = $"{tenant}:{StateKeys.TenantCostBudget}";
        await stateStore.SetAsync(key, request.DailyBudget, cancellationToken);

        return Results.Ok(new BudgetResponse(request.DailyBudget));
    }

    private static async Task<IResult> GetUnitBudgetAsync(
        string unitId,
        IStateStore stateStore,
        CancellationToken cancellationToken)
    {
        var key = $"{unitId}:{StateKeys.UnitCostBudget}";
        var budget = await stateStore.GetAsync<decimal?>(key, cancellationToken);

        if (budget is null)
        {
            return Results.Problem(detail: $"No budget set for unit '{unitId}'", statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new BudgetResponse(budget.Value));
    }

    private static async Task<IResult> SetUnitBudgetAsync(
        string unitId,
        SetBudgetRequest request,
        IStateStore stateStore,
        CancellationToken cancellationToken)
    {
        if (request.DailyBudget <= 0)
        {
            return Results.Problem(detail: "DailyBudget must be greater than zero", statusCode: StatusCodes.Status400BadRequest);
        }

        var key = $"{unitId}:{StateKeys.UnitCostBudget}";
        await stateStore.SetAsync(key, request.DailyBudget, cancellationToken);

        return Results.Ok(new BudgetResponse(request.DailyBudget));
    }
}