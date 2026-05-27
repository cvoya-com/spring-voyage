// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Snapshot of tenant-wide message interactions over a time window. Powers
/// the portal's Interactions visualization under <c>/activity</c>: a graph of
/// addressable nodes (agents, units, humans, connectors), the directed edges
/// between them, and a timeline histogram of sent counts per bucket.
/// </summary>
/// <param name="Nodes">
/// One row per unique addressable that appeared as sender or recipient in
/// the window. Truncated to the top-<c>cap</c> nodes by <c>Sent + Received</c>
/// when the unfiltered set exceeds the requested cap; edges referencing a
/// dropped node are dropped with it.
/// </param>
/// <param name="Edges">
/// One row per unique directed <c>(fromId, toId)</c> pair. Per ADR-0048 a
/// connector address is provenance-only: <c>toId</c> is never a connector.
/// </param>
/// <param name="Timeline">
/// Per-bucket histogram of <c>MessageSent</c> counts, broken down by
/// sender scheme (<c>agent</c>, <c>unit</c>, <c>human</c>, <c>connector</c>).
/// Empty buckets between the first and last observed bucket are zero-filled.
/// </param>
/// <param name="Truncated">
/// Populated when the unfiltered node count exceeded <c>cap</c>. Reports
/// the total observed and the kept count so the portal can surface the
/// truncation explicitly. <c>null</c> when no truncation occurred.
/// </param>
public record InteractionsGraph(
    IReadOnlyList<InteractionsNode> Nodes,
    IReadOnlyList<InteractionsEdge> Edges,
    IReadOnlyList<InteractionsTimelineBucket> Timeline,
    InteractionsTruncation? Truncated);

/// <summary>
/// One node in the interactions graph — a unique addressable observed as
/// sender, recipient, or both within the requested window.
/// </summary>
/// <param name="Id">
/// The addressable's canonical no-dash 32-hex Guid (<see cref="Identifiers.GuidFormatter"/>).
/// </param>
/// <param name="Kind">
/// The address scheme: <c>agent</c>, <c>unit</c>, <c>human</c>, or
/// <c>connector</c>. Per ADR-0048 connector nodes can appear only as a
/// sender (provenance) — never as a recipient.
/// </param>
/// <param name="DisplayName">
/// The current human-readable label resolved through
/// <see cref="Cvoya.Spring.Core.Security.IParticipantDisplayNameResolver"/>;
/// falls back to a per-scheme generic when the live row is missing.
/// </param>
/// <param name="Sent">Count of domain messages sent from this node in the window.</param>
/// <param name="Received">Count of domain messages received at this node in the window.</param>
public record InteractionsNode(
    string Id,
    string Kind,
    string DisplayName,
    long Sent,
    long Received);

/// <summary>
/// One directed edge in the interactions graph — every distinct
/// <c>(fromId, toId)</c> pair the window contains.
/// </summary>
/// <param name="FromId">Canonical no-dash 32-hex Guid of the sender.</param>
/// <param name="ToId">
/// Canonical no-dash 32-hex Guid of the recipient. Never a connector id —
/// per ADR-0048 the connector scheme is source-only.
/// </param>
/// <param name="Count">Number of domain messages on this edge in the window.</param>
/// <param name="FirstAt">Timestamp of the first message on this edge in the window.</param>
/// <param name="LastAt">Timestamp of the most recent message on this edge in the window.</param>
/// <param name="Channels">
/// Distinct recipient schemes observed on this edge — gives the portal a
/// hint of what kind of route the messages travelled (agent / unit / human).
/// Always one entry today; reserved as a list so future per-edge multi-route
/// observation (e.g. a unit fan-out address that resolves to multiple
/// schemes) does not need a schema change.
/// </param>
public record InteractionsEdge(
    string FromId,
    string ToId,
    long Count,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt,
    IReadOnlyList<string> Channels);

/// <summary>
/// One bucket in the timeline histogram — total messages sent inside
/// <c>[Bucket, Bucket + bucketSize)</c>, plus a per-sender-kind breakdown.
/// </summary>
/// <param name="Bucket">
/// Inclusive UTC start of the bucket, aligned to the requested bucket size
/// (hour-of-day for <c>hour</c>, midnight UTC for <c>day</c>).
/// </param>
/// <param name="Sent">Total <c>MessageSent</c>-equivalent message count in this bucket.</param>
/// <param name="ByKind">
/// Per-sender-kind breakdown. Keys are <c>agent</c>, <c>unit</c>,
/// <c>human</c>, <c>connector</c>; any kind not present in the bucket has
/// value <c>0</c>. Sum of the values equals <see cref="Sent"/>.
/// </param>
public record InteractionsTimelineBucket(
    DateTimeOffset Bucket,
    long Sent,
    IReadOnlyDictionary<string, long> ByKind);

/// <summary>
/// Truncation payload — only populated when the unfiltered node count
/// exceeded the requested <c>cap</c>.
/// </summary>
/// <param name="Total">Total distinct nodes observed in the window before truncation.</param>
/// <param name="Kept">Count of nodes kept after applying the cap.</param>
public record InteractionsTruncation(
    long Total,
    long Kept);

/// <summary>
/// Pulse-level truncation payload — populated only on the history
/// endpoint and only when the caller-supplied <c>maxPulses</c> budget
/// was exceeded. The history endpoint truncates from the oldest end of
/// the window so the most recent activity is preserved.
/// </summary>
/// <param name="Total">Total pulses the window contained before truncation.</param>
/// <param name="Kept">Pulses kept after truncation (always &lt;= <c>maxPulses</c>).</param>
public record InteractionsPulseTruncation(
    long Total,
    long Kept);

/// <summary>
/// One pulse in the history endpoint's per-message timeline — the
/// individual record of a single message landing on an edge. Distinct
/// from the snapshot's edge aggregate (which carries <c>count</c>) and
/// from the SSE pulse frame (which coalesces bursts on a per-edge
/// timer): the rewind-mode timeline animates messages one by one so
/// the operator can scrub backwards through the activity exactly as
/// it happened.
/// </summary>
/// <param name="Id">
/// Canonical no-dash 32-hex Guid of the message id (matches the
/// <c>messages.id</c> column persisted by ADR-0030 / ADR-0040). Stable
/// across reloads — the portal uses it as a React key.
/// </param>
/// <param name="FromId">Canonical no-dash 32-hex Guid of the sender.</param>
/// <param name="ToId">
/// Canonical no-dash 32-hex Guid of the recipient. Never a connector id
/// — per ADR-0048 the connector scheme is source-only.
/// </param>
/// <param name="Timestamp">Wall-clock UTC timestamp the dispatcher accepted the message.</param>
/// <param name="ThreadId">
/// Thread the message landed on. <c>null</c> for the (rare) legacy
/// message rows that pre-date the thread foreign key — the column is
/// non-null on new dispatches but reading is forgiving.
/// </param>
/// <param name="Channel">
/// Recipient scheme (<c>agent</c>, <c>unit</c>, <c>human</c>) — the
/// portal renders it as the edge's channel hint. Mirrors the edge
/// aggregate's per-row contribution to <see cref="InteractionsEdge.Channels"/>.
/// </param>
public record InteractionsPulse(
    string Id,
    string FromId,
    string ToId,
    DateTimeOffset Timestamp,
    string? ThreadId,
    string Channel);

/// <summary>
/// History snapshot returned by
/// <see cref="IInteractionsQueryService.GetHistoryAsync"/> — the same
/// nodes / edges as the snapshot endpoint produces, plus a per-message
/// pulse stream for the requested window so the portal's rewind mode
/// can scrub through the activity message by message.
/// </summary>
/// <param name="Nodes">
/// One row per unique addressable that appeared as sender or recipient.
/// Same shape and truncation rule as the snapshot endpoint — the
/// node-level <c>cap</c> still applies independently of the pulse budget.
/// </param>
/// <param name="Edges">
/// One row per unique directed <c>(fromId, toId)</c> pair. Same shape and
/// connector-source-only rule as the snapshot endpoint.
/// </param>
/// <param name="Pulses">
/// One entry per individual message — never coalesced. Sorted by
/// <see cref="InteractionsPulse.Timestamp"/> ascending; ties broken by
/// <see cref="InteractionsPulse.Id"/> lexically (canonical no-dash
/// 32-hex form) for determinism.
/// </param>
/// <param name="Truncated">
/// Truncation envelope. Carries the node-level total / kept counters
/// (same as the snapshot endpoint) and, when pulse truncation also
/// fires, a nested <see cref="InteractionsPulseTruncation"/> reporting
/// the pulse-level totals. <c>null</c> when neither truncation branch
/// fired.
/// </param>
public record InteractionsHistory(
    IReadOnlyList<InteractionsNode> Nodes,
    IReadOnlyList<InteractionsEdge> Edges,
    IReadOnlyList<InteractionsPulse> Pulses,
    InteractionsHistoryTruncation? Truncated);

/// <summary>
/// Truncation envelope for the history endpoint. Same node-level total /
/// kept as <see cref="InteractionsTruncation"/>, plus an optional nested
/// pulse-truncation block so a single payload covers both branches —
/// either or both can fire on the same response.
/// </summary>
/// <param name="Total">Distinct nodes observed before node-level truncation.</param>
/// <param name="Kept">Nodes kept after applying the node cap.</param>
/// <param name="Pulses">
/// Populated when pulse truncation also fired; <c>null</c> when only the
/// node cap fired (or neither did and the envelope was suppressed
/// upstream).
/// </param>
public record InteractionsHistoryTruncation(
    long Total,
    long Kept,
    InteractionsPulseTruncation? Pulses);

/// <summary>
/// Bucket granularity for <see cref="InteractionsGraph.Timeline"/>.
/// </summary>
public enum InteractionsBucket
{
    /// <summary>One bucket per hour, aligned to the hour boundary in UTC.</summary>
    Hour,

    /// <summary>One bucket per day, aligned to UTC midnight.</summary>
    Day,
}

/// <summary>
/// Filter parameters for <see cref="IInteractionsQueryService.GetAsync"/>.
/// </summary>
/// <param name="Since">Inclusive lower bound of the time window.</param>
/// <param name="Until">Exclusive upper bound of the time window.</param>
/// <param name="Unit">
/// Optional scope: include only edges that touch this addressable id
/// (sender or recipient). Compared after parsing the entity id; bare Guids
/// and addresses (<c>unit:<guid></c>) are both accepted by the API host
/// layer that builds this filter.
/// </param>
/// <param name="Participant">
/// Optional scope: include only edges that touch this addressable id
/// (sender or recipient). Distinct from <see cref="Unit"/> only by intent —
/// the underlying filter is identical; the API host layer accepts both
/// names so callers can express either "narrow to this unit" or "narrow
/// to this participant" without the server having to disambiguate.
/// </param>
/// <param name="Neighbours">
/// Hop depth applied around <see cref="Unit"/> / <see cref="Participant"/>.
/// <c>0</c> = only the scoped node's direct edges (the scoped node itself);
/// <c>1</c> = scoped + every direct neighbour; <c>2</c> = scoped + first
/// and second-degree neighbours (the default). Ignored when neither
/// scoping parameter is provided.
/// </param>
/// <param name="Bucket">Granularity of the timeline buckets.</param>
/// <param name="Cap">
/// Maximum number of nodes returned. When the unfiltered count exceeds
/// <see cref="Cap"/>, the top-<see cref="Cap"/> nodes by
/// <c>Sent + Received</c> are kept; edges referencing dropped nodes drop
/// too. <c>null</c> disables the cap.
/// </param>
public record InteractionsQueryFilters(
    DateTimeOffset Since,
    DateTimeOffset Until,
    Guid? Unit,
    Guid? Participant,
    int Neighbours,
    InteractionsBucket Bucket,
    int? Cap);

/// <summary>
/// Filter parameters for <see cref="IInteractionsQueryService.GetHistoryAsync"/>.
/// Same scoping + window + cap fields as <see cref="InteractionsQueryFilters"/>,
/// plus a per-pulse <see cref="MaxPulses"/> budget. The history surface
/// does not carry a <see cref="InteractionsBucket"/> — the per-message
/// pulse stream is the timeline and bucketing is the snapshot's job.
/// </summary>
/// <param name="Since">Inclusive lower bound of the time window.</param>
/// <param name="Until">Exclusive upper bound of the time window.</param>
/// <param name="Unit">
/// Optional scope: include only rows touching this addressable id. Same
/// semantics as <see cref="InteractionsQueryFilters.Unit"/>.
/// </param>
/// <param name="Participant">
/// Optional scope: include only rows touching this addressable id. Same
/// semantics as <see cref="InteractionsQueryFilters.Participant"/>.
/// </param>
/// <param name="Neighbours">
/// Hop depth applied around <see cref="Unit"/> / <see cref="Participant"/>.
/// Same semantics as <see cref="InteractionsQueryFilters.Neighbours"/>.
/// </param>
/// <param name="Cap">
/// Maximum number of nodes returned. Same semantics as
/// <see cref="InteractionsQueryFilters.Cap"/>; <c>null</c> disables.
/// </param>
/// <param name="MaxPulses">
/// Maximum pulses returned. When the window contains more pulses than
/// <see cref="MaxPulses"/>, the OLDEST pulses are dropped so the most
/// recent activity stays in the returned slice; the response carries an
/// <see cref="InteractionsPulseTruncation"/> reporting the totals.
/// </param>
public record InteractionsHistoryFilters(
    DateTimeOffset Since,
    DateTimeOffset Until,
    Guid? Unit,
    Guid? Participant,
    int Neighbours,
    int? Cap,
    int MaxPulses);
