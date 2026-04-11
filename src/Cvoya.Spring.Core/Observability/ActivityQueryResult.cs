// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Paginated result of an activity event query.
/// </summary>
/// <param name="Items">The activity event items on this page.</param>
/// <param name="TotalCount">The total number of matching events.</param>
/// <param name="Page">The current page number.</param>
/// <param name="PageSize">The number of items per page.</param>
public record ActivityQueryResult(IReadOnlyList<ActivityQueryResult.Item> Items, int TotalCount, int Page, int PageSize)
{
    /// <summary>
    /// A single activity event item.
    /// </summary>
    /// <param name="Id">The unique identifier.</param>
    /// <param name="Source">The event source.</param>
    /// <param name="EventType">The type of event.</param>
    /// <param name="Severity">The severity level.</param>
    /// <param name="Summary">A summary of the event.</param>
    /// <param name="CorrelationId">An optional correlation identifier.</param>
    /// <param name="Cost">An optional cost associated with the event.</param>
    /// <param name="Timestamp">When the event occurred.</param>
    public record Item(Guid Id, string Source, string EventType, string Severity, string Summary, string? CorrelationId, decimal? Cost, DateTimeOffset Timestamp);
}