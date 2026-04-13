// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Costs;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

using static Cvoya.Spring.Core.Costs.CostSource;

/// <summary>
/// Provides aggregated cost queries by querying <see cref="CostRecord"/> entities
/// from the database. Registered as a scoped service because it depends on <see cref="SpringDbContext"/>.
/// </summary>
public class CostAggregation(SpringDbContext dbContext) : ICostQueryService
{
    /// <inheritdoc />
    public async Task<CostSummary> GetAgentCostAsync(string agentId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CostRecords
            .Where(r => r.AgentId == agentId && r.Timestamp >= from && r.Timestamp <= to);

        return await AggregateAsync(query, from, to, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CostSummary> GetUnitCostAsync(string unitId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CostRecords
            .Where(r => r.UnitId == unitId && r.Timestamp >= from && r.Timestamp <= to);

        return await AggregateAsync(query, from, to, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CostSummary> GetTenantCostAsync(string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CostRecords
            .Where(r => r.TenantId == tenantId && r.Timestamp >= from && r.Timestamp <= to);

        return await AggregateAsync(query, from, to, cancellationToken);
    }

    private static async Task<CostSummary> AggregateAsync(
        IQueryable<CostRecord> query,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var records = await query.ToListAsync(cancellationToken);

        return new CostSummary(
            TotalCost: records.Sum(r => r.Cost),
            TotalInputTokens: records.Sum(r => (long)r.InputTokens),
            TotalOutputTokens: records.Sum(r => (long)r.OutputTokens),
            RecordCount: records.Count,
            WorkCost: records.Where(r => r.Source == Work).Sum(r => r.Cost),
            InitiativeCost: records.Where(r => r.Source == Initiative).Sum(r => r.Cost),
            From: from,
            To: to);
    }
}