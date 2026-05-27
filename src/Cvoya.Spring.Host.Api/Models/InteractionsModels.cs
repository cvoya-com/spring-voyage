// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Query-string binding for <c>GET /api/v1/tenant/observation/interactions</c>
/// (#2867). All parameters are optional with server-side defaults documented
/// per property. The endpoint reads tenant-wide; the participant + unit
/// scoping filters are convenience narrowing, not authorisation boundaries —
/// the <see cref="Cvoya.Spring.Core.Security.PlatformRoles.TenantObserver"/>
/// gate is what grants visibility into threads the caller is not a
/// participant of.
/// </summary>
/// <param name="Since">
/// Inclusive lower bound of the window. Default: <c>now − 10 minutes</c>.
/// Accepts any ISO 8601 timestamp.
/// </param>
/// <param name="Until">
/// Exclusive upper bound of the window. Default: <c>now</c>.
/// </param>
/// <param name="Unit">
/// Narrow to interactions touching this addressable id. Accepted shapes:
/// canonical <c>scheme:hex</c> address, bare Guid (dashed or no-dash), or
/// the legacy <c>scheme://hex</c> URI form. The scheme is recorded but
/// ignored — the filter applies to the id only, so a tag of <c>unit:</c>
/// and <c>agent:</c> with the same id behave identically.
/// </param>
/// <param name="Participant">
/// Same semantics as <see cref="Unit"/> — separated only because callers
/// express the two intents with different vocabulary (one narrows to a
/// unit, the other to an arbitrary participant). The server unions both
/// filters when both are supplied.
/// </param>
/// <param name="Neighbours">
/// Hop depth applied around <see cref="Unit"/> / <see cref="Participant"/>.
/// Default <c>2</c>; valid values <c>0</c>, <c>1</c>, <c>2</c>. Out-of-
/// range values are clamped to the valid range.
/// </param>
/// <param name="Bucket">
/// Timeline granularity. <c>hour</c> (default) or <c>day</c>. Anything
/// else falls back to the default with no error.
/// </param>
/// <param name="Cap">
/// Maximum nodes returned; top-N by <c>sent + received</c>. Accepts an
/// integer (default <c>50</c>) or the literal <c>none</c> to disable
/// truncation entirely.
/// </param>
public record InteractionsQuery(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    string? Unit,
    string? Participant,
    int? Neighbours,
    string? Bucket,
    string? Cap);

/// <summary>
/// Snapshot DTO returned by <c>GET /api/v1/tenant/observation/interactions</c>.
/// Mirrors the Core <see cref="Cvoya.Spring.Core.Observability.InteractionsGraph"/>
/// but kept as a host-layer DTO so the OpenAPI / Kiota contract has stable
/// camelCase property names.
/// </summary>
/// <param name="Nodes">One entry per addressable seen as sender or recipient.</param>
/// <param name="Edges">One entry per distinct <c>(fromId, toId)</c> pair.</param>
/// <param name="Timeline">
/// Per-bucket histogram of sent counts, zero-filled between the window's
/// first and last bucket.
/// </param>
/// <param name="Truncated">
/// Populated when the unfiltered node count exceeded <c>cap</c>. Omitted
/// from the serialised payload when the result fits within the cap, per
/// <c>JsonIgnoreCondition.WhenWritingNull</c>.
/// </param>
public record InteractionsGraphResponse(
    IReadOnlyList<InteractionsNodeResponse> Nodes,
    IReadOnlyList<InteractionsEdgeResponse> Edges,
    IReadOnlyList<InteractionsTimelineBucketResponse> Timeline,
    InteractionsTruncationResponse? Truncated);

/// <summary>One node in the interactions graph.</summary>
/// <param name="Id">Canonical no-dash 32-hex Guid.</param>
/// <param name="Kind">Address scheme: <c>agent</c>, <c>unit</c>, <c>human</c>, or <c>connector</c>.</param>
/// <param name="DisplayName">Resolved live display name; falls back to a per-scheme generic.</param>
/// <param name="Sent">Messages sent in the window.</param>
/// <param name="Received">Messages received in the window.</param>
public record InteractionsNodeResponse(
    string Id,
    string Kind,
    string DisplayName,
    long Sent,
    long Received);

/// <summary>One directed edge in the interactions graph.</summary>
/// <param name="FromId">Canonical Guid of the sender.</param>
/// <param name="ToId">Canonical Guid of the recipient. Per ADR-0048 never a connector id.</param>
/// <param name="Count">Number of messages on this edge.</param>
/// <param name="FirstAt">Timestamp of the first message.</param>
/// <param name="LastAt">Timestamp of the last message.</param>
/// <param name="Channels">Distinct recipient schemes observed on this edge.</param>
public record InteractionsEdgeResponse(
    string FromId,
    string ToId,
    long Count,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt,
    IReadOnlyList<string> Channels);

/// <summary>One bucket in the timeline histogram.</summary>
/// <param name="Bucket">Inclusive UTC start of the bucket.</param>
/// <param name="Sent">Total messages sent inside <c>[Bucket, Bucket + bucketSize)</c>.</param>
/// <param name="ByKind">Per-sender-kind breakdown; sum equals <see cref="Sent"/>.</param>
public record InteractionsTimelineBucketResponse(
    DateTimeOffset Bucket,
    long Sent,
    IReadOnlyDictionary<string, long> ByKind);

/// <summary>Truncation payload — emitted only when the unfiltered node count exceeded <c>cap</c>.</summary>
/// <param name="Total">Distinct nodes observed before truncation.</param>
/// <param name="Kept">Nodes kept after applying the cap.</param>
public record InteractionsTruncationResponse(
    long Total,
    long Kept);

/// <summary>
/// Query-string binding for <c>GET /api/v1/tenant/observation/interactions/history</c>
/// (#2872). Same scope / window / cap surface as
/// <see cref="InteractionsQuery"/>, minus the <c>Bucket</c> field (the
/// per-message pulse stream is the history's timeline) and plus a
/// <see cref="MaxPulses"/> budget. Defaults match the issue: a 10-minute
/// window ending at <c>now</c>, <c>neighbours=2</c>, <c>maxPulses=5000</c>,
/// <c>cap=50</c>.
/// </summary>
/// <param name="Since">
/// Inclusive lower bound of the window. Default: <c>now − 10 minutes</c>.
/// Accepts any ISO 8601 timestamp.
/// </param>
/// <param name="Until">
/// Exclusive upper bound of the window. Default: <c>now</c>.
/// </param>
/// <param name="Unit">
/// Narrow to interactions touching this addressable id. Accepts the same
/// shapes as <see cref="InteractionsQuery.Unit"/>.
/// </param>
/// <param name="Participant">
/// Narrow to interactions touching this addressable id. Accepts the same
/// shapes as <see cref="InteractionsQuery.Participant"/>.
/// </param>
/// <param name="Neighbours">
/// Hop depth applied around <see cref="Unit"/> / <see cref="Participant"/>.
/// Default <c>2</c>; valid values <c>0</c>, <c>1</c>, <c>2</c>. Out-of-
/// range values are clamped to the valid range.
/// </param>
/// <param name="MaxPulses">
/// Maximum pulses returned. Default <c>5000</c>. Values <c>&lt;= 0</c>
/// fall back to the default. When the window contains more pulses than
/// the budget, the OLDEST pulses are dropped so the most recent activity
/// stays in the returned slice and the response carries a
/// <see cref="InteractionsPulseTruncationResponse"/>.
/// </param>
/// <param name="Cap">
/// Maximum nodes returned; top-N by <c>sent + received</c>. Accepts an
/// integer (default <c>50</c>) or the literal <c>none</c> to disable
/// truncation entirely.
/// </param>
public record InteractionsHistoryQuery(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    string? Unit,
    string? Participant,
    int? Neighbours,
    int? MaxPulses,
    string? Cap);

/// <summary>
/// History DTO returned by <c>GET /api/v1/tenant/observation/interactions/history</c>.
/// Same nodes / edges as the snapshot endpoint produces (with the same
/// truncation rule), plus an ordered per-message pulse list and a
/// truncation envelope that covers both branches (node-level and
/// pulse-level) when either fires.
/// </summary>
/// <param name="Nodes">One entry per addressable seen as sender or recipient.</param>
/// <param name="Edges">One entry per distinct <c>(fromId, toId)</c> pair.</param>
/// <param name="Pulses">
/// One entry per individual Domain message in the window. Sorted by
/// <c>timestamp</c> ascending; ties broken by <c>messageId</c>.
/// </param>
/// <param name="Truncated">
/// Truncation envelope — present whenever either the node-level cap or
/// the pulse-level budget fired. <c>null</c> otherwise.
/// </param>
public record InteractionsHistoryResponse(
    IReadOnlyList<InteractionsNodeResponse> Nodes,
    IReadOnlyList<InteractionsEdgeResponse> Edges,
    IReadOnlyList<InteractionsPulseResponse> Pulses,
    InteractionsHistoryTruncationResponse? Truncated);

/// <summary>
/// One pulse in the history endpoint's timeline — a single Domain message
/// landing on an edge. Never coalesced.
/// </summary>
/// <param name="MessageId">Canonical no-dash 32-hex message id.</param>
/// <param name="FromId">Canonical Guid of the sender.</param>
/// <param name="ToId">Canonical Guid of the recipient. Per ADR-0048 never a connector id.</param>
/// <param name="Timestamp">Wall-clock UTC timestamp the dispatcher accepted the message.</param>
/// <param name="ThreadId">Thread the message landed on (<c>null</c> only for legacy rows that pre-date the FK).</param>
/// <param name="Channel">Recipient scheme — the portal renders it as the edge's channel hint.</param>
public record InteractionsPulseResponse(
    string MessageId,
    string FromId,
    string ToId,
    DateTimeOffset Timestamp,
    string? ThreadId,
    string Channel);

/// <summary>
/// History truncation envelope. Carries the node-level total / kept (same
/// as the snapshot endpoint) plus, when pulse truncation also fired, a
/// nested <see cref="InteractionsPulseTruncationResponse"/>.
/// </summary>
/// <param name="Total">Distinct nodes observed before node-level truncation.</param>
/// <param name="Kept">Nodes kept after applying the node cap.</param>
/// <param name="Pulses">
/// Pulse-level truncation, when it fired; <c>null</c> when only the node
/// branch fired or it was suppressed entirely.
/// </param>
public record InteractionsHistoryTruncationResponse(
    long Total,
    long Kept,
    InteractionsPulseTruncationResponse? Pulses);

/// <summary>
/// Pulse-level truncation payload — emitted on the history endpoint only
/// when the window contained more pulses than the caller's budget.
/// </summary>
/// <param name="Total">Pulses the window contained before truncation.</param>
/// <param name="Kept">Pulses kept after truncation (always <= the budget).</param>
public record InteractionsPulseTruncationResponse(
    long Total,
    long Kept);
