// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Text.Json;

using Cvoya.Spring.Core.Observability;

/// <summary>
/// API-layer enriched version of <see cref="InboxItem"/>. The <c>from</c>
/// field is promoted from a bare address string to a <see cref="ParticipantRef"/>
/// carrying both the address and the resolved display name. All other fields
/// are identical to the Core domain model.
/// </summary>
/// <param name="ThreadId">The thread the ask belongs to.</param>
/// <param name="From">The participant that last spoke on the thread, enriched with a display name.</param>
/// <param name="Human">The <c>human://</c> address this row belongs to, enriched with a display name.</param>
/// <param name="PendingSince">Timestamp of the ask event.</param>
/// <param name="Summary">Human-readable summary of the ask.</param>
/// <param name="UnreadCount">Number of unread events on the thread since the human's last read cursor.</param>
public record InboxItemResponse(
    string ThreadId,
    ParticipantRef From,
    ParticipantRef Human,
    DateTimeOffset PendingSince,
    string Summary,
    int UnreadCount = 0);

/// <summary>
/// API-layer enriched version of <see cref="ThreadSummary"/>. The
/// <c>participants</c> array is promoted from bare address strings to
/// <see cref="ParticipantRef"/> objects, and the <c>origin</c> field
/// likewise carries a display name alongside the address.
/// </summary>
/// <param name="Id">The thread identifier.</param>
/// <param name="Participants">Distinct participants, each enriched with a display name.</param>
/// <param name="Status">Lifecycle state — <c>active</c> or <c>completed</c>.</param>
/// <param name="LastActivity">Timestamp of the most recent event on this thread.</param>
/// <param name="CreatedAt">Timestamp of the first event on this thread.</param>
/// <param name="EventCount">Number of activity events observed for this thread.</param>
/// <param name="Origin">The address that emitted the first event, enriched with a display name.</param>
/// <param name="Summary">Human-readable summary — the first message's summary text, truncated.</param>
public record ThreadSummaryResponse(
    string Id,
    IReadOnlyList<ParticipantRef> Participants,
    string Status,
    DateTimeOffset LastActivity,
    DateTimeOffset CreatedAt,
    int EventCount,
    ParticipantRef Origin,
    string Summary);

/// <summary>
/// API-layer enriched version of <see cref="ThreadEvent"/>. The <c>source</c>,
/// <c>from</c>, and <c>to</c> fields are promoted to <see cref="ParticipantRef"/>
/// objects when non-null.
/// </summary>
/// <param name="Id">The activity event identifier.</param>
/// <param name="Timestamp">When the event occurred.</param>
/// <param name="Source">The participant that emitted the event, enriched with a display name.</param>
/// <param name="EventType">The event type name.</param>
/// <param name="Severity">The severity level.</param>
/// <param name="Summary">Human-readable summary of the event.</param>
/// <param name="MessageId">The message id this event corresponds to, or <c>null</c>.</param>
/// <param name="From">The sender of the underlying message, enriched with a display name, or <c>null</c>.</param>
/// <param name="To">The recipient address string of the underlying message, or <c>null</c>.</param>
/// <param name="Body">The rendered text body of the underlying message, or <c>null</c>.</param>
public record ThreadEventResponse(
    Guid Id,
    DateTimeOffset Timestamp,
    ParticipantRef Source,
    string EventType,
    string Severity,
    string Summary,
    Guid? MessageId = null,
    ParticipantRef? From = null,
    string? To = null,
    string? Body = null);

/// <summary>
/// API-layer enriched version of <see cref="ThreadDetail"/>. Contains
/// <see cref="ThreadSummaryResponse"/> and the ordered list of
/// <see cref="ThreadEventResponse"/> items.
/// </summary>
/// <param name="Summary">The enriched thread summary.</param>
/// <param name="Events">The ordered enriched activity events.</param>
public record ThreadDetailResponse(
    ThreadSummaryResponse Summary,
    IReadOnlyList<ThreadEventResponse> Events);