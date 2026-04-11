// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Queries activity events from the database with support for filtering, pagination,
/// and cost aggregation.
/// </summary>
public class ActivityQueryService(SpringDbContext dbContext) : IActivityQueryService
{
    /// <inheritdoc />
    public async Task<ActivityQueryResult> QueryAsync(ActivityQueryParameters parameters, CancellationToken cancellationToken)
    {
        var query = dbContext.ActivityEvents.AsQueryable();

        if (!string.IsNullOrEmpty(parameters.Source))
        {
            query = query.Where(e => e.Source == parameters.Source);
        }

        if (!string.IsNullOrEmpty(parameters.EventType))
        {
            query = query.Where(e => e.EventType == parameters.EventType);
        }

        if (!string.IsNullOrEmpty(parameters.Severity))
        {
            query = query.Where(e => e.Severity == parameters.Severity);
        }

        if (parameters.From.HasValue)
        {
            query = query.Where(e => e.Timestamp >= parameters.From.Value);
        }

        if (parameters.To.HasValue)
        {
            query = query.Where(e => e.Timestamp <= parameters.To.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(e => e.Timestamp)
            .Skip((parameters.Page - 1) * parameters.PageSize)
            .Take(parameters.PageSize)
            .Select(e => new ActivityQueryResult.Item(
                e.Id, e.Source, e.EventType, e.Severity, e.Summary,
                e.CorrelationId, e.Cost, e.Timestamp))
            .ToListAsync(cancellationToken);

        return new ActivityQueryResult(items, totalCount, parameters.Page, parameters.PageSize);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActivityQueryResult.Item>> GetRecentAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        return await dbContext.ActivityEvents
            .Where(e => e.Timestamp > since)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => new ActivityQueryResult.Item(
                e.Id, e.Source, e.EventType, e.Severity, e.Summary,
                e.CorrelationId, e.Cost, e.Timestamp))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<decimal> GetTotalCostAsync(string? source, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var query = dbContext.ActivityEvents.AsQueryable();

        if (!string.IsNullOrEmpty(source))
        {
            query = query.Where(e => e.Source == source);
        }

        if (from.HasValue)
        {
            query = query.Where(e => e.Timestamp >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(e => e.Timestamp <= to.Value);
        }

        return await query.SumAsync(e => e.Cost ?? 0, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CostBySource>> GetCostBySourceAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var query = dbContext.ActivityEvents.AsQueryable();

        if (from.HasValue)
        {
            query = query.Where(e => e.Timestamp >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(e => e.Timestamp <= to.Value);
        }

        return await query
            .GroupBy(e => e.Source)
            .Select(g => new CostBySource(g.Key, g.Sum(e => e.Cost ?? 0)))
            .ToListAsync(cancellationToken);
    }
}