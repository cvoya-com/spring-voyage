// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Maps cost-related API endpoints for querying aggregated cost data per agent, unit, and tenant.
/// </summary>
public static class CostEndpoints
{
    /// <summary>
    /// Registers cost endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapCostEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/cost")
            .WithTags("Costs");

        group.MapGet("/agents/{id}", GetAgentCostAsync)
            .WithName("GetAgentCost")
            .WithSummary("Get cost summary for an agent")
            .Produces<CostSummaryResponse>(StatusCodes.Status200OK);

        group.MapGet("/agents/{id}/breakdown", GetAgentCostBreakdownAsync)
            .WithName("GetAgentCostBreakdown")
            .WithSummary("Get per-model cost breakdown for an agent")
            .Produces<CostBreakdownResponse>(StatusCodes.Status200OK);

        group.MapGet("/units/{id}", GetUnitCostAsync)
            .WithName("GetUnitCost")
            .WithSummary("Get cost summary for a unit")
            .Produces<CostSummaryResponse>(StatusCodes.Status200OK);

        group.MapGet("/tenant", GetTenantCostAsync)
            .WithName("GetTenantCost")
            .WithSummary("Get cost summary for the tenant")
            .Produces<CostSummaryResponse>(StatusCodes.Status200OK);

        return group;
    }

    private static async Task<IResult> GetAgentCostAsync(
        string id,
        ICostQueryService costQueryService,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(id, out var idGuid))
        {
            return Results.Problem(detail: $"Agent id '{id}' is not a valid Guid.", statusCode: StatusCodes.Status400BadRequest);
        }
        var (rangeFrom, rangeTo) = ResolveTimeRange(from, to);
        var summary = await costQueryService.GetAgentCostAsync(idGuid, rangeFrom, rangeTo, cancellationToken);
        return Results.Ok(ToResponse(summary));
    }

    private static async Task<IResult> GetAgentCostBreakdownAsync(
        string id,
        ICostQueryService costQueryService,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(id, out var idGuid))
        {
            return Results.Problem(detail: $"Agent id '{id}' is not a valid Guid.", statusCode: StatusCodes.Status400BadRequest);
        }
        var (rangeFrom, rangeTo) = ResolveTimeRange(from, to);
        var entries = await costQueryService.GetAgentCostBreakdownAsync(idGuid, rangeFrom, rangeTo, cancellationToken);
        var response = new CostBreakdownResponse(
            AgentId: id,
            From: rangeFrom,
            To: rangeTo,
            Entries: entries
                .Select(e => new CostBreakdownEntryResponse(e.Key, e.Kind, e.TotalCost, e.RecordCount))
                .ToList());
        return Results.Ok(response);
    }

    private static async Task<IResult> GetUnitCostAsync(
        string id,
        ICostQueryService costQueryService,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(id, out var idGuid))
        {
            return Results.Problem(detail: $"Unit id '{id}' is not a valid Guid.", statusCode: StatusCodes.Status400BadRequest);
        }
        var (rangeFrom, rangeTo) = ResolveTimeRange(from, to);
        var summary = await costQueryService.GetUnitCostAsync(idGuid, rangeFrom, rangeTo, cancellationToken);
        return Results.Ok(ToResponse(summary));
    }

    private static async Task<IResult> GetTenantCostAsync(
        ICostQueryService costQueryService,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var tenant = !string.IsNullOrWhiteSpace(tenantId)
            && Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(tenantId, out var parsed)
                ? parsed
                : Cvoya.Spring.Core.Tenancy.OssTenantIds.Default;
        var (rangeFrom, rangeTo) = ResolveTimeRange(from, to);
        var summary = await costQueryService.GetTenantCostAsync(tenant, rangeFrom, rangeTo, cancellationToken);
        return Results.Ok(ToResponse(summary));
    }

    private static (DateTimeOffset From, DateTimeOffset To) ResolveTimeRange(DateTimeOffset? from, DateTimeOffset? to)
    {
        var rangeTo = to ?? DateTimeOffset.UtcNow;
        var rangeFrom = from ?? rangeTo.AddDays(-30);
        return (rangeFrom, rangeTo);
    }

    private static CostSummaryResponse ToResponse(CostSummary summary) =>
        new(
            summary.TotalCost,
            summary.TotalInputTokens,
            summary.TotalOutputTokens,
            summary.RecordCount,
            summary.WorkCost,
            summary.InitiativeCost,
            summary.From,
            summary.To);
}
