// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Summary row for the thread list surface (<c>GET /api/v1/threads</c>).
/// Derived from the activity-event stream — no separate thread table exists:
/// a thread is materialised from every event that carries the same
/// <see cref="ActivityQueryResult.Item.CorrelationId"/> (the platform persists the
/// <c>ThreadId</c> of each message as the correlation id on observability
/// events, per the messaging architecture).
/// </summary>
/// <param name="Id">The thread identifier (the correlation id on the activity events).</param>
/// <param name="Participants">Distinct source addresses (<c>scheme://path</c>) that emitted events on this thread.</param>
/// <param name="Status">Lifecycle state — <c>active</c> until a terminal <c>ThreadCompleted</c> event arrives, otherwise <c>completed</c>.</param>
/// <param name="LastActivity">Timestamp of the most recent event on this thread.</param>
/// <param name="CreatedAt">Timestamp of the first event on this thread.</param>
/// <param name="EventCount">Number of activity events observed for this thread.</param>
/// <param name="Origin">The address (<c>scheme://path</c>) that emitted the first event on this thread — where the thread started.</param>
/// <param name="Summary">Human-readable summary — the first message's summary text, truncated.</param>
public record ThreadSummary(
    string Id,
    IReadOnlyList<string> Participants,
    string Status,
    DateTimeOffset LastActivity,
    DateTimeOffset CreatedAt,
    int EventCount,
    string Origin,
    string Summary);

/// <summary>
/// Detailed thread payload for <c>GET /api/v1/threads/{id}</c>.
/// Carries the summary row plus the ordered event timeline so the CLI (and later
/// the portal) can render the thread with role attribution.
/// </summary>
/// <param name="Summary">The list-level summary row for this thread.</param>
/// <param name="Events">The ordered activity events that form the thread.</param>
public record ThreadDetail(
    ThreadSummary Summary,
    IReadOnlyList<ThreadEvent> Events);

/// <summary>
/// One activity event as rendered on a thread. This is a
/// flattened projection of <see cref="Capabilities.ActivityEvent"/>, scoped to
/// the columns the thread UI / CLI needs so we do not have to leak the
/// activity domain shape into the thread wire contract.
/// </summary>
/// <param name="Id">The activity event identifier.</param>
/// <param name="Timestamp">When the event occurred.</param>
/// <param name="Source">The <c>scheme://path</c> address that emitted the event.</param>
/// <param name="EventType">The event type name.</param>
/// <param name="Severity">The severity level.</param>
/// <param name="Summary">Human-readable summary of the event.</param>
/// <param name="MessageId">The message id this event corresponds to (for <c>MessageReceived</c> events), or <c>null</c>.</param>
/// <param name="From">The sender address (<c>scheme://path</c>) of the underlying message, or <c>null</c>.</param>
/// <param name="To">The recipient address of the underlying message, or <c>null</c>.</param>
/// <param name="Body">The rendered text body of the underlying message when extractable, or <c>null</c> for non-text payloads.</param>
public record ThreadEvent(
    Guid Id,
    DateTimeOffset Timestamp,
    string Source,
    string EventType,
    string Severity,
    string Summary,
    Guid? MessageId = null,
    string? From = null,
    string? To = null,
    string? Body = null);

/// <summary>
/// One row in a human's inbox (<c>GET /api/v1/inbox</c>). A thread shows
/// up here when the human has a <c>MessageReceived</c> on the thread and no
/// non-human actor has observed a <c>MessageReceived</c> after that point —
/// i.e. "an agent said something to me and I haven't replied yet". The
/// predicate is intentionally tolerant of trailing observability events
/// (state changes, cost emissions) on the same thread; only a follow-up
/// <c>MessageReceived</c> from another participant clears the row (#1210).
/// Responding via <c>POST /api/v1/threads/{id}/messages</c> (or the
/// CLI's <c>spring inbox respond</c>) removes the row by causing exactly that
/// follow-up event.
/// </summary>
/// <param name="ThreadId">The thread the ask belongs to.</param>
/// <param name="From">The <c>scheme://path</c> of the actor that last spoke on the thread (the requester).</param>
/// <param name="Human">The <c>human://</c> address this row belongs to.</param>
/// <param name="PendingSince">Timestamp of the ask event.</param>
/// <param name="Summary">Human-readable summary of the ask — the last event's summary text.</param>
public record InboxItem(
    string ThreadId,
    string From,
    string Human,
    DateTimeOffset PendingSince,
    string Summary);

/// <summary>
/// Filters for <see cref="IThreadQueryService.ListAsync"/>. Each filter is
/// optional; omitted values match all threads. <see cref="Limit"/> caps the
/// returned row count — the default matches the activity page size (50).
/// </summary>
/// <param name="Unit">Restrict to threads whose origin source is the named unit (matches unit-scheme events).</param>
/// <param name="Agent">Restrict to threads whose origin source is the named agent.</param>
/// <param name="Status"><c>active</c> or <c>completed</c>.</param>
/// <param name="Participant">Restrict to threads where <c>scheme://path</c> appears as a participant.</param>
/// <param name="Limit">Maximum number of rows to return (default 50).</param>
public record ThreadQueryFilters(
    string? Unit = null,
    string? Agent = null,
    string? Status = null,
    string? Participant = null,
    int? Limit = null);