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
