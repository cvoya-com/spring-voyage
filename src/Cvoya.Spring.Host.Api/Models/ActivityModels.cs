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
/// <param name="ExternalForward">
/// External OTel-backend forwarding block (#2503). Null when forwarding
/// is unconfigured. Header values are redacted to <c>***</c> on the
/// wire so the GET surface never echoes back live credentials.
/// </param>
public sealed record TenantActivitySettingsDto(
    [property: System.Text.Json.Serialization.JsonPropertyName("level")] string Level,
    [property: System.Text.Json.Serialization.JsonPropertyName("retention_days")] int RetentionDays,
    [property: System.Text.Json.Serialization.JsonPropertyName("external_forward")] ExternalForwardDto? ExternalForward = null)
{
    /// <summary>Projects a domain snapshot into the wire shape.</summary>
    public static TenantActivitySettingsDto FromSnapshot(
        Cvoya.Spring.Core.Capabilities.TenantActivitySettingsSnapshot snapshot)
    {
        ExternalForwardDto? forward = null;
        if (snapshot.ExternalForward is { } cfg)
        {
            // Redact every header value — only the keys are echoed back
            // so operators can confirm a header set is configured
            // without leaking the credential itself.
            var safeHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in cfg.Headers.Keys)
            {
                safeHeaders[key] = "***";
            }
            forward = new ExternalForwardDto(
                Endpoint: cfg.Endpoint,
                Protocol: cfg.Protocol,
                Headers: safeHeaders,
                Enabled: cfg.Enabled);
        }
        return new TenantActivitySettingsDto(
            snapshot.Level.ToString().ToLowerInvariant(),
            snapshot.RetentionDays,
            forward);
    }
}

/// <summary>External-forward block returned by the GET endpoint.</summary>
/// <param name="Endpoint">OTLP/HTTP base URL.</param>
/// <param name="Protocol"><c>http/json</c> | <c>http/protobuf</c>.</param>
/// <param name="Headers">
/// Header key/value map. Values are masked (<c>***</c>) on the GET
/// surface; updates pass clear values via PATCH.
/// </param>
/// <param name="Enabled">Master toggle.</param>
public sealed record ExternalForwardDto(
    [property: System.Text.Json.Serialization.JsonPropertyName("endpoint")] string Endpoint,
    [property: System.Text.Json.Serialization.JsonPropertyName("protocol")] string Protocol,
    [property: System.Text.Json.Serialization.JsonPropertyName("headers")] IReadOnlyDictionary<string, string> Headers,
    [property: System.Text.Json.Serialization.JsonPropertyName("enabled")] bool Enabled);

/// <summary>
/// PATCH body for the tenant activity-settings endpoint. Any field can
/// be null to leave the current value unchanged. Issue #2492 + #2503.
/// </summary>
/// <param name="Level">Optional new capture level; null to leave unchanged.</param>
/// <param name="RetentionDays">Optional new retention horizon; null to leave unchanged.</param>
/// <param name="ExternalForward">
/// Optional new external-forward block. <c>{ "clear": true }</c> clears
/// an existing block; supplying an endpoint sets the block.
/// </param>
public sealed record UpdateTenantActivitySettingsRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("level")] string? Level,
    [property: System.Text.Json.Serialization.JsonPropertyName("retention_days")] int? RetentionDays,
    [property: System.Text.Json.Serialization.JsonPropertyName("external_forward")] ExternalForwardUpdateRequest? ExternalForward = null);

/// <summary>Wire shape for updating the external forwarder.</summary>
public sealed record ExternalForwardUpdateRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("endpoint")] string? Endpoint,
    [property: System.Text.Json.Serialization.JsonPropertyName("protocol")] string? Protocol,
    [property: System.Text.Json.Serialization.JsonPropertyName("headers")] Dictionary<string, string>? Headers,
    [property: System.Text.Json.Serialization.JsonPropertyName("enabled")] bool? Enabled,
    [property: System.Text.Json.Serialization.JsonPropertyName("clear")] bool? Clear);

/// <summary>
/// Wire shape returned by <c>GET /api/v1/tenant/activity/settings/forward-status</c>
/// (#2503). Reflects the most-recent forward attempt classification
/// for the current tenant.
/// </summary>
/// <param name="Kind"><c>success</c> | <c>failure</c> | <c>disabled</c>.</param>
/// <param name="ObservedAt">When the result was recorded (null if never attempted).</param>
/// <param name="Message">Free-text detail on failure; null for success / disabled.</param>
public sealed record TenantActivityForwardStatusDto(
    [property: System.Text.Json.Serialization.JsonPropertyName("kind")] string Kind,
    [property: System.Text.Json.Serialization.JsonPropertyName("observed_at")] DateTimeOffset? ObservedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string? Message);
