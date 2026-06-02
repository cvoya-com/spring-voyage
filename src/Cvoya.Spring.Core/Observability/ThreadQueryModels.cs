// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Summary row for the thread list surface (<c>GET /api/v1/threads</c>).
/// Derived from the EF-authoritative <c>threads</c> + <c>messages</c> tables
/// (ADR-0030 / ADR-0040): the thread row carries identity, participants, and
/// timestamps; the message rows for that thread feed the per-thread aggregates
/// (<see cref="EventCount"/>, <see cref="Origin"/>, <see cref="Summary"/>).
/// Per ADR-0030 a thread is a lifelong record with no thread-level lifecycle
/// state — the only state machine in the model is per-(thread, participant).
/// </summary>
/// <param name="Id">The thread identifier (no-dash 32-char hex Guid).</param>
/// <param name="Participants">Canonical participant addresses (<c>scheme:&lt;32-hex&gt;</c>, matching <see cref="Cvoya.Spring.Core.Messaging.Address.ToString"/>) for the thread. Identity comparisons should parse the Guid via <see cref="Cvoya.Spring.Core.Messaging.AddressIdentity.TryGetActorId"/> rather than string-comparing.</param>
/// <param name="LastActivity">Timestamp of the most recent message on this thread (<c>threads.last_activity_at</c>).</param>
/// <param name="CreatedAt">Timestamp the thread row was first inserted (<c>threads.created_at</c>).</param>
/// <param name="EventCount">Number of persisted messages on this thread.</param>
/// <param name="Origin">The canonical address (<c>scheme:&lt;32-hex&gt;</c>) that sent the first message on this thread.</param>
/// <param name="Summary">Human-readable summary — the first message's body text, when extractable.</param>
/// <param name="ParticipantNameSnapshots">
/// Snapshot of each participant's display name at message-write time
/// (#2533). Keyed by canonical address string. The enrichment path
/// substitutes this value when the live resolver returns a per-scheme
/// generic fallback (the underlying definition was soft-deleted) so
/// the engagement list keeps showing the last-known real name.
/// Defaults to an empty map.
/// </param>
/// <param name="IsArchived">
/// Derived flag (#2732) — <c>true</c> when every non-human participant
/// has been soft-deleted (or is missing) so the human user cannot
/// realistically take any further action on the thread. The default
/// engagement list filters these out; the portal exposes a separate
/// archive surface keyed off this flag. A thread with no non-human
/// participants (e.g. a solo human row) is never archived — the empty
/// "every non-human" set is vacuously true, but the orphan rule
/// requires at least one non-human participant on the thread.
/// </param>
/// <param name="RecipientHumanId">
/// ADR-0062 § 5 (#2826): the Hat that received the latest inbound on
/// this thread. Resolved from the most recent persisted message whose
/// recipient scheme is <c>human:</c>. <c>null</c> when no message on
/// the thread was addressed to a human recipient (pure A2A threads, or
/// threads whose only human-addressed messages were sent by the human
/// themselves). The portal reads this field to render the per-row Hat
/// chip — the same chip the inbox surface already shows — on the
/// engagement list and the unit / agent messaging-tab.
/// </param>
public record ThreadSummary(
    string Id,
    IReadOnlyList<string> Participants,
    DateTimeOffset LastActivity,
    DateTimeOffset CreatedAt,
    int EventCount,
    string Origin,
    string Summary,
    IReadOnlyDictionary<string, string>? ParticipantNameSnapshots = null,
    bool IsArchived = false,
    Guid? RecipientHumanId = null);

/// <summary>
/// Detailed thread payload for <c>GET /api/v1/threads/{id}</c>.
/// Carries the summary row plus the ordered message timeline so the CLI (and
/// later the portal) can render the thread with role attribution.
/// </summary>
/// <param name="Summary">The list-level summary row for this thread.</param>
/// <param name="Events">The ordered messages that form the thread timeline.</param>
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
/// <param name="MessageId">The message id this event corresponds to (for <c>MessageArrived</c> events), or <c>null</c>.</param>
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
/// up here when the human has a <c>MessageArrived</c> on the thread and no
/// non-human actor has observed a <c>MessageArrived</c> after that point —
/// i.e. "an agent said something to me and I haven't replied yet". The
/// predicate is intentionally tolerant of trailing observability events
/// (state changes, cost emissions) on the same thread; only a follow-up
/// <c>MessageArrived</c> from another participant clears the row (#1210).
/// Responding via <c>POST /api/v1/threads/{id}/messages</c> (or the
/// CLI's <c>spring inbox respond</c>) removes the row by causing exactly that
/// follow-up event.
/// </summary>
/// <param name="ThreadId">The thread the ask belongs to.</param>
/// <param name="From">The <c>scheme://path</c> of the actor that last spoke on the thread (the requester).</param>
/// <param name="Human">The <c>human://</c> address this row belongs to.</param>
/// <param name="PendingSince">Timestamp of the ask event.</param>
/// <param name="Summary">Human-readable summary of the ask — the last event's summary text.</param>
/// <param name="UnreadCount">
/// Number of thread events whose timestamp is strictly greater than the
/// human's <c>lastReadAt</c> for this thread. Defaults to <c>0</c> when
/// the caller omits last-read data. The badge in the portal reads this
/// field directly — it is always non-negative.
/// </param>
/// <param name="ParticipantNameSnapshots">
/// Snapshot of each participant's display name at message-write time
/// (#2533). Keyed by canonical address string. The enrichment path
/// substitutes this value when the live resolver returns a per-scheme
/// generic fallback so the inbox list keeps showing the last-known real
/// name. Defaults to an empty map.
/// </param>
public record InboxItem(
    string ThreadId,
    string From,
    string Human,
    DateTimeOffset PendingSince,
    string Summary,
    int UnreadCount = 0,
    IReadOnlyDictionary<string, string>? ParticipantNameSnapshots = null);

/// <summary>
/// Filters for <see cref="IThreadQueryService.ListAsync"/>. Each filter is
/// optional; omitted values match all threads. <see cref="Limit"/> caps the
/// returned row count — the default matches the activity page size (50).
/// </summary>
/// <param name="Unit">Restrict to threads whose origin source is the named unit (matches unit-scheme events).</param>
/// <param name="Agent">Restrict to threads whose origin source is the named agent.</param>
/// <param name="Participant">Restrict to threads where <c>scheme://path</c> appears as a participant.</param>
/// <param name="Limit">Maximum number of rows to return (default 50).</param>
/// <param name="Archived">
/// Archive-state filter (#2732). <c>null</c> or <c>false</c> excludes
/// archived (fully-orphaned) threads from the response — the default
/// engagement list stays uncluttered. <c>true</c> returns ONLY archived
/// threads — drives the portal's separate archive surface. The check is
/// derived per-thread from <see cref="ThreadSummary.IsArchived"/>.
/// </param>
/// <param name="Since">
/// Lower bound on <see cref="ThreadSummary.LastActivity"/> (#2790).
/// When supplied, only threads whose last activity is at or after this
/// instant are returned. Omitted matches every thread regardless of
/// recency. Drives the portal Conversations view's "since" date filter
/// and the matching <c>spring conversations list --since</c> CLI flag.
/// </param>
public record ThreadQueryFilters(
    string? Unit = null,
    string? Agent = null,
    string? Participant = null,
    int? Limit = null,
    bool? Archived = null,
    DateTimeOffset? Since = null);

/// <summary>
/// A per-message projection scoped to what a caller may read. Returned by
/// <see cref="IThreadQueryService.SearchAsync"/> (one row per free-text hit)
/// and by <see cref="IThreadQueryService.GetMessagesByIdsAsync"/> (one row
/// per resolved-and-authorised id). Mirrors the per-message columns on
/// <see cref="ThreadEvent"/> with the containing thread id added so callers
/// can stitch the row back to a thread without a second query.
/// </summary>
/// <param name="ThreadId">The thread the message belongs to (no-dash 32-char hex Guid).</param>
/// <param name="MessageId">The message id.</param>
/// <param name="Timestamp">When the message was sent.</param>
/// <param name="From">Sender address in canonical <c>scheme:&lt;32-hex&gt;</c> form.</param>
/// <param name="To">Recipient address in canonical <c>scheme:&lt;32-hex&gt;</c> form.</param>
/// <param name="Body">The rendered text body of the message.</param>
public record ThreadSearchHit(
    string ThreadId,
    Guid MessageId,
    DateTimeOffset Timestamp,
    string From,
    string To,
    string Body);

/// <summary>
/// Result of <see cref="IThreadQueryService.GetMessagesByIdsAsync"/> — the
/// by-id complement to <see cref="IThreadQueryService.SearchAsync"/>
/// (#2990). <see cref="Messages"/> carries the requested messages the caller
/// is allowed to read (it participates in the message's thread), in the
/// order the ids were requested. <see cref="Skipped"/> carries every
/// requested id that did not come back — unknown, on a thread the caller
/// does not participate in, foreign-tenant, or syntactically malformed.
/// </summary>
/// <remarks>
/// The two skip reasons (does-not-exist vs not-a-participant) are
/// deliberately <b>collapsed</b> into one <see cref="Skipped"/> bucket so the
/// surface never leaks the difference between a message that is absent and
/// one the caller simply may not see (@savasp on #2990). Each skipped entry
/// echoes the id string exactly as the caller supplied it.
/// </remarks>
/// <param name="Messages">The authorised messages, in requested order.</param>
/// <param name="Skipped">The requested ids that were not returned, in requested order.</param>
public record MessageLookup(
    IReadOnlyList<ThreadSearchHit> Messages,
    IReadOnlyList<string> Skipped);
