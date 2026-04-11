// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

/// <summary>
/// Represents a persisted activity event record.
/// Activity events capture observable actions within the platform for audit, analytics, and debugging.
/// </summary>
public class ActivityEventRecord
{
    /// <summary>Gets or sets the unique identifier for the activity event.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the source address of the event (e.g., "agent:ada").</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the type of the event.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Gets or sets the severity level of the event.</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>Gets or sets a human-readable summary of the event.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets additional event details stored as JSON.</summary>
    public JsonElement? Details { get; set; }

    /// <summary>Gets or sets the correlation identifier for tracing related events.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Gets or sets the cost associated with this event (e.g., token usage).</summary>
    public decimal? Cost { get; set; }

    /// <summary>Gets or sets the timestamp when the event occurred.</summary>
    public DateTimeOffset Timestamp { get; set; }

}