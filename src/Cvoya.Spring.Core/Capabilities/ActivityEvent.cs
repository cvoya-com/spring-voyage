// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents an observable activity event emitted by a component.
/// </summary>
/// <param name="Id">The unique identifier of the event.</param>
/// <param name="Timestamp">The timestamp when the event occurred.</param>
/// <param name="Source">The address of the component that emitted the event.</param>
/// <param name="EventType">The typed category of this activity event.</param>
/// <param name="Severity">The severity level of this event.</param>
/// <param name="Summary">A human-readable one-liner describing the event.</param>
/// <param name="Details">Structured payload with additional event data.</param>
/// <param name="CorrelationId">Traces related events across the system.</param>
/// <param name="Cost">LLM cost if applicable.</param>
public record ActivityEvent(
    Guid Id,
    DateTimeOffset Timestamp,
    Address Source,
    ActivityEventType EventType,
    ActivitySeverity Severity,
    string Summary,
    JsonElement? Details = null,
    string? CorrelationId = null,
    decimal? Cost = null);
