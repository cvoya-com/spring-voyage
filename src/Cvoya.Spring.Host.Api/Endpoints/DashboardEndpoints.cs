// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Maps dashboard API endpoints for agent, unit, and cost summaries.
/// </summary>
public static class DashboardEndpoints
{
    /// <summary>
    /// Registers dashboard endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/dashboard")
            .WithTags("Dashboard");

        group.MapGet("/agents", GetAgentsSummaryAsync)
            .WithName("GetAgentsSummary")
            .WithSummary("Get a summary of all registered agents");

        group.MapGet("/units", GetUnitsSummaryAsync)
            .WithName("GetUnitsSummary")
            .WithSummary("Get a summary of all registered units");

        group.MapGet("/costs", GetCostsSummaryAsync)
            .WithName("GetCostsSummary")
            .WithSummary("Get aggregated cost data");

        return group;
    }

    private static async Task<IResult> GetAgentsSummaryAsync(
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var entries = await directoryService.ListAllAsync(cancellationToken);

        var agents = entries
            .Where(e => string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
            .Select(e => new AgentDashboardSummary(e.Address.Path, e.DisplayName, e.Role, e.RegisteredAt))
            .ToList();

        return Results.Ok(agents);
    }

    private static async Task<IResult> GetUnitsSummaryAsync(
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var entries = await directoryService.ListAllAsync(cancellationToken);

        var units = entries
            .Where(e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            .Select(e => new UnitDashboardSummary(e.Address.Path, e.DisplayName, e.RegisteredAt))
            .ToList();

        return Results.Ok(units);
    }

    private static async Task<IResult> GetCostsSummaryAsync(
        IActivityQueryService queryService,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var costsBySource = await queryService.GetCostBySourceAsync(from, to, cancellationToken);
        var totalCost = await queryService.GetTotalCostAsync(null, from, to, cancellationToken);

        var summary = new CostDashboardSummary(totalCost, costsBySource, from, to);
        return Results.Ok(summary);
    }
}