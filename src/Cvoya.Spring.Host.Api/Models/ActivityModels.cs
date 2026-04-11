// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// DTO for binding activity query parameters from the query string.
/// </summary>
/// <param name="Source">Optional filter by event source.</param>
/// <param name="EventType">Optional filter by event type.</param>
/// <param name="Severity">Optional filter by severity level.</param>
/// <param name="From">Optional start of time range.</param>
/// <param name="To">Optional end of time range.</param>
/// <param name="Page">Page number (1-based).</param>
/// <param name="PageSize">Number of items per page.</param>
public record ActivityQueryParametersDto(
    string? Source,
    string? EventType,
    string? Severity,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int? Page,
    int? PageSize);