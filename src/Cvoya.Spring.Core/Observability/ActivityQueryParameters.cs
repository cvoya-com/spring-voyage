// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Parameters for querying activity events with optional filters and pagination.
/// </summary>
/// <param name="Source">Optional filter by event source.</param>
/// <param name="EventType">Optional filter by event type.</param>
/// <param name="Severity">Optional filter by severity level.</param>
/// <param name="From">Optional start of time range.</param>
/// <param name="To">Optional end of time range.</param>
/// <param name="Page">Page number (1-based).</param>
/// <param name="PageSize">Number of items per page.</param>
public record ActivityQueryParameters(
    string? Source = null,
    string? EventType = null,
    string? Severity = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50);