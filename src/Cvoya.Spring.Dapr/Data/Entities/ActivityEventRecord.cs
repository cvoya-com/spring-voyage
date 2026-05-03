// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Represents a persisted activity event record.
/// Activity events capture observable actions within the platform for
/// audit, analytics, and debugging.
///
/// <para>
/// <see cref="SourceId"/> stores the Guid of the entity that emitted the
/// event. UI and audit-rendering code joins to the live entity tables to
/// resolve a current display name; renames never invalidate history.
/// </para>
/// </summary>
public class ActivityEventRecord : ITenantScopedEntity
{
    /// <summary>Gets or sets the unique identifier for the activity event.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant that owns this activity event.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Gets or sets the source's stable Guid id (the agent / unit / human
    /// that emitted this event). The display name is rendered at read
    /// time by joining the live entity table.
    /// </summary>
    public Guid SourceId { get; set; }

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
