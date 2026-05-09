// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Queries activity events from the database with support for filtering, pagination,
/// and cost aggregation.
///
/// <para>
/// The persisted shape stores only a Guid <c>SourceId</c>; this service
/// emits and accepts the canonical no-dash 32-char hex form on its public
/// surface (string-typed <see cref="ActivityQueryParameters.Source"/> and
/// <see cref="ActivityQueryResult.Item.Source"/>) so the wire format
/// stays grep-able and stable.
/// </para>
/// <para>
/// <c>Source</c> may arrive as either a bare GUID or a scheme-prefixed
/// string such as <c>unit:&lt;guid&gt;</c> or <c>unit://&lt;guid&gt;</c>
/// (the form returned by <c>ActivitySourceNormalizer</c>).  The service
/// strips the scheme prefix before parsing so both shapes filter
/// correctly against <c>SourceId</c>.
/// </para>
/// </summary>
public class ActivityQueryService(SpringDbContext dbContext) : IActivityQueryService
{
    /// <inheritdoc />
    public async Task<ActivityQueryResult> QueryAsync(ActivityQueryParameters parameters, CancellationToken cancellationToken)
    {
        var query = dbContext.ActivityEvents.AsQueryable();

        if (!string.IsNullOrEmpty(parameters.Source) && TryExtractSourceId(parameters.Source, out var sourceId))
        {
            query = query.Where(e => e.SourceId == sourceId);
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

        var rawItems = await query
            .OrderByDescending(e => e.Timestamp)
            .Skip((parameters.Page - 1) * parameters.PageSize)
            .Take(parameters.PageSize)
            .Select(e => new { e.Id, e.SourceId, e.EventType, e.Severity, e.Summary, e.CorrelationId, e.Cost, e.Timestamp, e.Details })
            .ToListAsync(cancellationToken);

        // #1665: include the persisted Details payload so the portal's
        // Activity tab can expand a row to see the full structured payload
        // (validation code/message, transition context, etc.) without
        // falling back to the SSE stream — historical rows were previously
        // unrecoverable from the REST surface.
        var items = rawItems
            .Select(e => new ActivityQueryResult.Item(
                e.Id, GuidFormatter.Format(e.SourceId), e.EventType, e.Severity, e.Summary,
                e.CorrelationId, e.Cost, e.Timestamp, e.Details))
            .ToList();

        return new ActivityQueryResult(items, totalCount, parameters.Page, parameters.PageSize);
    }

    /// <inheritdoc />
    public async Task<decimal> GetTotalCostAsync(string? source, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var query = dbContext.ActivityEvents.AsQueryable();

        if (!string.IsNullOrEmpty(source) && TryExtractSourceId(source, out var sourceId))
        {
            query = query.Where(e => e.SourceId == sourceId);
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

        var grouped = await query
            .GroupBy(e => e.SourceId)
            .Select(g => new { SourceId = g.Key, TotalCost = g.Sum(e => e.Cost ?? 0) })
            .ToListAsync(cancellationToken);

        return grouped
            .Select(g => new CostBySource(GuidFormatter.Format(g.SourceId), g.TotalCost))
            .ToList();
    }

    /// <summary>
    /// Extracts a <see cref="Guid"/> from a source string that is either a bare GUID
    /// (<c>8c5fab2a…</c>) or a scheme-prefixed form (<c>unit:8c5fab2a…</c> /
    /// <c>unit://8c5fab2a…</c>) emitted by <c>ActivitySourceNormalizer</c>.
    /// </summary>
    private static bool TryExtractSourceId(string source, out Guid result)
    {
        var raw = source;
        var colonIdx = raw.IndexOf(':');
        if (colonIdx >= 0)
        {
            raw = raw[(colonIdx + 1)..];
            if (raw.StartsWith("//", StringComparison.Ordinal))
                raw = raw[2..];
        }

        return GuidFormatter.TryParse(raw, out result);
    }
}
