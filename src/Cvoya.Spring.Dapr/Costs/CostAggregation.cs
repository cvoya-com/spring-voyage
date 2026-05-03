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
    public async Task<CostSummary> GetAgentCostAsync(Guid agentId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CostRecords
            .Where(r => r.AgentId == agentId && r.Timestamp >= from && r.Timestamp <= to);

        return await AggregateAsync(query, from, to, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CostSummary> GetUnitCostAsync(Guid unitId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CostRecords
            .Where(r => r.UnitId == unitId && r.Timestamp >= from && r.Timestamp <= to);

        return await AggregateAsync(query, from, to, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CostSummary> GetTenantCostAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CostRecords
            .Where(r => r.TenantId == tenantId && r.Timestamp >= from && r.Timestamp <= to);

        return await AggregateAsync(query, from, to, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CostTimeseries> GetTenantCostTimeseriesAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucket,
        string bucketLabel,
        CancellationToken cancellationToken = default)
    {
        if (bucket <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bucket), "bucket must be positive.");
        }

        if (to <= from)
        {
            throw new ArgumentException("'to' must be strictly after 'from'.", nameof(to));
        }

        // Bucket count rounds up so partial trailing windows (e.g. a 30d
        // window viewed 15 minutes after a bucket edge) still render the
        // in-progress bucket rather than dropping it.
        var windowTicks = (to - from).Ticks;
        var bucketTicks = bucket.Ticks;
        var bucketCount = (int)((windowTicks + bucketTicks - 1) / bucketTicks);

        // Pull just the (timestamp, cost) columns we need. The table is
        // append-only and modest per tenant at v2.0 scale, so a single scan
        // + in-memory bucketing is cheaper than an EF-translated GROUP BY
        // with provider-specific date arithmetic.
        var rows = await dbContext.CostRecords
            .Where(r => r.TenantId == tenantId && r.Timestamp >= from && r.Timestamp < to)
            .Select(r => new { r.Timestamp, r.Cost })
            .ToListAsync(cancellationToken);

        var buckets = new decimal[bucketCount];
        foreach (var row in rows)
        {
            // Anchor on `from`; bucket index = floor((ts - from) / bucket).
            // Works on UTC wall-clock ticks, so DST transitions never move
            // an event into an unexpected bucket.
            var offsetTicks = (row.Timestamp - from).Ticks;
            var idx = (int)(offsetTicks / bucketTicks);
            if (idx < 0 || idx >= bucketCount)
            {
                continue;
            }
            buckets[idx] += row.Cost;
        }

        var series = new List<CostTimeseriesBucket>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
        {
            series.Add(new CostTimeseriesBucket(
                BucketStart: from + TimeSpan.FromTicks(bucketTicks * i),
                Cost: buckets[i]));
        }

        return new CostTimeseries(from, to, bucketLabel, series);
    }

    /// <inheritdoc />
    public async Task<CostTimeseries> GetAgentCostTimeseriesAsync(
        Guid agentId,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucket,
        string bucketLabel,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.CostRecords
            .Where(r => r.AgentId == agentId && r.Timestamp >= from && r.Timestamp < to)
            .Select(r => new { r.Timestamp, r.Cost })
            .ToListAsync(cancellationToken);

        return BuildTimeseries(rows.Select(r => (r.Timestamp, r.Cost)), from, to, bucket, bucketLabel);
    }

    /// <inheritdoc />
    public async Task<CostTimeseries> GetUnitCostTimeseriesAsync(
        Guid unitId,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucket,
        string bucketLabel,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.CostRecords
            .Where(r => r.UnitId == unitId && r.Timestamp >= from && r.Timestamp < to)
            .Select(r => new { r.Timestamp, r.Cost })
            .ToListAsync(cancellationToken);

        return BuildTimeseries(rows.Select(r => (r.Timestamp, r.Cost)), from, to, bucket, bucketLabel);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CostBreakdownEntry>> GetAgentCostBreakdownAsync(
        Guid agentId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var records = await dbContext.CostRecords
            .Where(r => r.AgentId == agentId && r.Timestamp >= from && r.Timestamp <= to)
            .Select(r => new { r.Model, r.Cost })
            .ToListAsync(cancellationToken);

        return records
            .GroupBy(r => r.Model)
            .Select(g => new CostBreakdownEntry(
                Key: g.Key,
                Kind: "model",
                TotalCost: g.Sum(r => r.Cost),
                RecordCount: g.Count()))
            .OrderByDescending(e => e.TotalCost)
            .ToList();
    }

    // Shared bucketing logic. Callers materialise the EF query into a typed
    // list before calling so EF Core does not need to translate the tuple
    // projection — it stays in-memory LINQ only.
    private static CostTimeseries BuildTimeseries(
        IEnumerable<(DateTimeOffset Timestamp, decimal Cost)> rows,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucket,
        string bucketLabel)
    {
        if (bucket <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bucket), "bucket must be positive.");
        }

        if (to <= from)
        {
            throw new ArgumentException("'to' must be strictly after 'from'.", nameof(to));
        }

        var windowTicks = (to - from).Ticks;
        var bucketTicks = bucket.Ticks;
        var bucketCount = (int)((windowTicks + bucketTicks - 1) / bucketTicks);

        var buckets = new decimal[bucketCount];
        foreach (var (timestamp, cost) in rows)
        {
            var offsetTicks = (timestamp - from).Ticks;
            var idx = (int)(offsetTicks / bucketTicks);
            if (idx < 0 || idx >= bucketCount)
            {
                continue;
            }
            buckets[idx] += cost;
        }

        var series = new List<CostTimeseriesBucket>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
        {
            series.Add(new CostTimeseriesBucket(
                BucketStart: from + TimeSpan.FromTicks(bucketTicks * i),
                Cost: buckets[i]));
        }

        return new CostTimeseries(from, to, bucketLabel, series);
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