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

/// <summary>
/// DTO returned by the tenant activity-settings endpoint. Mirrors the
/// platform's <c>TenantActivitySettingsSnapshot</c> shape with snake-case
/// wire names. Issue #2492.
/// </summary>
/// <param name="Level">Capture level: <c>off</c> | <c>summary</c> | <c>full</c>.</param>
/// <param name="RetentionDays">Retention horizon in days.</param>
public sealed record TenantActivitySettingsDto(
    [property: System.Text.Json.Serialization.JsonPropertyName("level")] string Level,
    [property: System.Text.Json.Serialization.JsonPropertyName("retention_days")] int RetentionDays)
{
    /// <summary>Projects a domain snapshot into the wire shape.</summary>
    public static TenantActivitySettingsDto FromSnapshot(
        Cvoya.Spring.Core.Capabilities.TenantActivitySettingsSnapshot snapshot)
        => new(snapshot.Level.ToString().ToLowerInvariant(), snapshot.RetentionDays);
}

/// <summary>
/// PATCH body for the tenant activity-settings endpoint. Either field
/// can be null to leave the current value unchanged. Issue #2492.
/// </summary>
/// <param name="Level">Optional new capture level; null to leave unchanged.</param>
/// <param name="RetentionDays">Optional new retention horizon; null to leave unchanged.</param>
public sealed record UpdateTenantActivitySettingsRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("level")] string? Level,
    [property: System.Text.Json.Serialization.JsonPropertyName("retention_days")] int? RetentionDays);
