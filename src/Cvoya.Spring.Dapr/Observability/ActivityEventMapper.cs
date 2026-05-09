// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data.Entities;

/// <summary>
/// Maps between the Core <see cref="ActivityEvent"/> domain record and the EF
/// <see cref="ActivityEventRecord"/> entity used for persistence.
///
/// <para>
/// The persisted record carries only the source's stable Guid id; the
/// scheme is not stored. Callers that need a typed <see cref="Address"/>
/// supply the scheme at read time. Out-of-band rendering layers (UI,
/// audit) join the live entity tables to resolve current display name.
/// </para>
/// </summary>
public static class ActivityEventMapper
{
    /// <summary>
    /// Converts a domain <see cref="ActivityEvent"/> to a persistence <see cref="ActivityEventRecord"/>.
    /// </summary>
    public static ActivityEventRecord ToRecord(ActivityEvent activityEvent)
    {
        return new ActivityEventRecord
        {
            Id = activityEvent.Id,
            SourceId = activityEvent.Source.Id,
            EventType = activityEvent.EventType.ToString(),
            Severity = activityEvent.Severity.ToString(),
            Summary = activityEvent.Summary,
            Details = activityEvent.Details,
            CorrelationId = activityEvent.CorrelationId,
            Cost = activityEvent.Cost,
            Timestamp = activityEvent.Timestamp,
        };
    }

    /// <summary>
    /// Converts a persistence <see cref="ActivityEventRecord"/> back to a
    /// domain <see cref="ActivityEvent"/>. Because the persisted record
    /// stores only <see cref="ActivityEventRecord.SourceId"/> (no scheme),
    /// callers that need a scheme-typed address supply it via
    /// <paramref name="defaultScheme"/>; the default <c>"agent"</c>
    /// matches the most common emission site.
    /// </summary>
    public static ActivityEvent ToDomain(ActivityEventRecord record, string defaultScheme = "agent")
    {
        var address = new Address(defaultScheme, record.SourceId);

        return new ActivityEvent(
            record.Id,
            record.Timestamp,
            address,
            Enum.Parse<ActivityEventType>(record.EventType),
            Enum.Parse<ActivitySeverity>(record.Severity),
            record.Summary,
            record.Details,
            record.CorrelationId,
            record.Cost);
    }
}
