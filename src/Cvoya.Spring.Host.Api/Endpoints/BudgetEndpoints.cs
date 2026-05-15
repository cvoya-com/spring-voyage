// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Maps budget management API endpoints for agents, units, and tenants.
/// All reads / writes route through the tenant-scoped <c>budget_limits</c>
/// EF table (ADR-0040 / #2045) — the pre-ADR actor-state keys
/// <c>Agent:CostBudget</c>, <c>Unit:CostBudget</c>, and
/// <c>Tenant:CostBudget</c> were removed in the same change. The current
/// tenant is taken from <see cref="ITenantContext.CurrentTenantId"/>; the
/// previous <c>tenantId ?? "default"</c> fallback (the multi-tenancy bug
/// closed by #2045) is gone — the EF query filter is now the only gate.
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
        // Budgets are operator-config — every group self-gates on the
        // TenantOperator role here so all three scopes (agent / tenant /
        // unit) inherit the gate uniformly. Previously the only gate
        // lived in Program.cs and chained off MapBudgetEndpoints' return
        // value, which exposed the tenant- and unit-scoped groups as
        // unauthenticated (issue #2288). The auth precedent matches
        // MapAuthEndpoints / MapConnectorEndpoints — auth lives inside
        // the endpoint file, not on the caller.
        var agentGroup = app.MapGroup("/api/v1/tenant/agents/{agentId}/budget")
            .WithTags("Budgets")
            .RequireAuthorization(RolePolicies.TenantOperator);

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
            .WithTags("Budgets")
            .RequireAuthorization(RolePolicies.TenantOperator);

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
            .WithTags("Budgets")
            .RequireAuthorization(RolePolicies.TenantOperator);

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
        SpringDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!GuidFormatter.TryParse(agentId, out var agentGuid))
        {
            return Results.Problem(
                detail: $"Agent id '{agentId}' is not a valid Guid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var row = await dbContext.BudgetLimits
            .AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.ScopeType == BudgetLimitScope.Agent && b.ScopeId == agentGuid,
                cancellationToken);

        if (row is null)
        {
            return Results.Problem(
                detail: $"No budget set for agent '{agentId}'",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new BudgetResponse(row.DailyBudget));
    }

    private static async Task<IResult> SetAgentBudgetAsync(
        string agentId,
        SetBudgetRequest request,
        SpringDbContext dbContext,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        if (request.DailyBudget <= 0)
        {
            return Results.Problem(
                detail: "DailyBudget must be greater than zero",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!GuidFormatter.TryParse(agentId, out var agentGuid))
        {
            return Results.Problem(
                detail: $"Agent id '{agentId}' is not a valid Guid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await UpsertAsync(
            dbContext,
            tenantContext,
            BudgetLimitScope.Agent,
            agentGuid,
            request.DailyBudget,
            cancellationToken);

        return Results.Ok(new BudgetResponse(request.DailyBudget));
    }

    private static async Task<IResult> GetTenantBudgetAsync(
        SpringDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.BudgetLimits
            .AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.ScopeType == BudgetLimitScope.Tenant && b.ScopeId == null,
                cancellationToken);

        if (row is null)
        {
            return Results.Problem(
                detail: "No budget set for tenant",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new BudgetResponse(row.DailyBudget));
    }

    private static async Task<IResult> SetTenantBudgetAsync(
        SetBudgetRequest request,
        SpringDbContext dbContext,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        if (request.DailyBudget <= 0)
        {
            return Results.Problem(
                detail: "DailyBudget must be greater than zero",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await UpsertAsync(
            dbContext,
            tenantContext,
            BudgetLimitScope.Tenant,
            scopeId: null,
            request.DailyBudget,
            cancellationToken);

        return Results.Ok(new BudgetResponse(request.DailyBudget));
    }

    private static async Task<IResult> GetUnitBudgetAsync(
        string unitId,
        SpringDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!GuidFormatter.TryParse(unitId, out var unitGuid))
        {
            return Results.Problem(
                detail: $"Unit id '{unitId}' is not a valid Guid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var row = await dbContext.BudgetLimits
            .AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.ScopeType == BudgetLimitScope.Unit && b.ScopeId == unitGuid,
                cancellationToken);

        if (row is null)
        {
            return Results.Problem(
                detail: $"No budget set for unit '{unitId}'",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new BudgetResponse(row.DailyBudget));
    }

    private static async Task<IResult> SetUnitBudgetAsync(
        string unitId,
        SetBudgetRequest request,
        SpringDbContext dbContext,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        if (request.DailyBudget <= 0)
        {
            return Results.Problem(
                detail: "DailyBudget must be greater than zero",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!GuidFormatter.TryParse(unitId, out var unitGuid))
        {
            return Results.Problem(
                detail: $"Unit id '{unitId}' is not a valid Guid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await UpsertAsync(
            dbContext,
            tenantContext,
            BudgetLimitScope.Unit,
            unitGuid,
            request.DailyBudget,
            cancellationToken);

        return Results.Ok(new BudgetResponse(request.DailyBudget));
    }

    private static async Task UpsertAsync(
        SpringDbContext dbContext,
        ITenantContext tenantContext,
        string scopeType,
        Guid? scopeId,
        decimal dailyBudget,
        CancellationToken cancellationToken)
    {
        // Find the existing row inside the tenant query filter — there is at
        // most one because of the partial unique indexes on
        // (tenant_id, scope_type, scope_id) for non-null scope_id and
        // (tenant_id, scope_type) where scope_id IS NULL for the tenant case.
        var existing = await dbContext.BudgetLimits
            .FirstOrDefaultAsync(
                b => b.ScopeType == scopeType && b.ScopeId == scopeId,
                cancellationToken);

        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            dbContext.BudgetLimits.Add(new BudgetLimitEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.CurrentTenantId,
                ScopeType = scopeType,
                ScopeId = scopeId,
                DailyBudget = dailyBudget,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.DailyBudget = dailyBudget;
            existing.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
