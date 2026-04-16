// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;

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

        group.MapGet("/summary", GetDashboardSummaryAsync)
            .WithName("GetDashboardSummary")
            .WithSummary("Get an aggregated dashboard summary with unit/agent counts, status breakdown, recent activity, and total cost")
            .Produces<DashboardSummary>(StatusCodes.Status200OK);

        group.MapGet("/agents", GetAgentsSummaryAsync)
            .WithName("GetAgentsSummary")
            .WithSummary("Get a summary of all registered agents")
            .Produces<AgentDashboardSummary[]>(StatusCodes.Status200OK);

        group.MapGet("/units", GetUnitsSummaryAsync)
            .WithName("GetUnitsSummary")
            .WithSummary("Get a summary of all registered units")
            .Produces<UnitDashboardSummary[]>(StatusCodes.Status200OK);

        group.MapGet("/costs", GetCostsSummaryAsync)
            .WithName("GetCostsSummary")
            .WithSummary("Get aggregated cost data")
            .Produces<CostDashboardSummary>(StatusCodes.Status200OK);

        return group;
    }

    private static async Task<IResult> GetDashboardSummaryAsync(
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IActivityQueryService activityQueryService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.DashboardEndpoints");
        var entries = await directoryService.ListAllAsync(cancellationToken);

        // Count agents.
        var agentCount = entries
            .Count(e => string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase));

        // Count units and gather status breakdown.
        var unitEntries = entries
            .Where(e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var statusCounts = new Dictionary<UnitStatus, int>();
        foreach (var e in unitEntries)
        {
            var status = UnitStatus.Draft;
            try
            {
                var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                    new ActorId(e.ActorId), nameof(UnitActor));
                status = await proxy.GetStatusAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to read status for unit {UnitName}; reporting Draft in dashboard summary.",
                    e.Address.Path);
            }

            statusCounts[status] = statusCounts.TryGetValue(status, out var count) ? count + 1 : 1;
        }

        // Recent activity (last 10).
        var activityResult = await activityQueryService.QueryAsync(
            new Core.Observability.ActivityQueryParameters(PageSize: 10),
            cancellationToken);

        // Total cost.
        var totalCost = await activityQueryService.GetTotalCostAsync(null, null, null, cancellationToken);

        var summary = new DashboardSummary(
            unitEntries.Count,
            statusCounts,
            agentCount,
            activityResult.Items,
            totalCost);

        return Results.Ok(summary);
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
        IActorProxyFactory actorProxyFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.DashboardEndpoints");
        var entries = await directoryService.ListAllAsync(cancellationToken);

        var unitEntries = entries
            .Where(e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var units = new List<UnitDashboardSummary>(unitEntries.Count);
        foreach (var e in unitEntries)
        {
            var status = UnitStatus.Draft;
            try
            {
                var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                    new ActorId(e.ActorId), nameof(UnitActor));
                status = await proxy.GetStatusAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to read status for unit {UnitName}; reporting Draft in dashboard.",
                    e.Address.Path);
            }

            units.Add(new UnitDashboardSummary(e.Address.Path, e.DisplayName, e.RegisteredAt, status));
        }

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