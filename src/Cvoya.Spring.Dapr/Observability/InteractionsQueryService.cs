// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF-Core-backed implementation of <see cref="IInteractionsQueryService"/>
/// (issue #2867). Reads from the <c>messages</c> table landed by ADR-0030 /
/// ADR-0040; the tenant query filter on <see cref="SpringDbContext"/>
/// restricts visibility to the calling tenant automatically.
/// </summary>
/// <remarks>
/// <para>
/// Aggregation runs in two passes: a server-side pull of every
/// <c>Domain</c> message in the window into a lightweight projection (the
/// rows the visualization needs are tiny — id, scheme + id pairs,
/// timestamp), then in-memory roll-up into nodes / edges / timeline. The
/// projection keeps the SQL portable across providers and lets the
/// neighbours filter run after the initial scan without a second
/// round-trip.
/// </para>
/// <para>
/// Per <see href="../../../docs/decisions/archive/0048-event-vs-request-message-semantics.md">ADR-0048</see>
/// the <c>connector</c> scheme is provenance-only. The persistence layer
/// is authoritative — <see cref="Cvoya.Spring.Core.Messaging.MessageRouter"/>
/// rejects sends to a connector recipient — so this projection trusts the
/// data and only filters defensively (a stray legacy row with a
/// connector recipient would not appear as a <c>toId</c> edge).
/// </para>
/// <para>
/// The history surface (<see cref="GetHistoryAsync"/>, #2872) reuses the
/// same projection + neighbours + cap helpers as the snapshot path. The
/// only path that differs is the pulse projection — the snapshot
/// aggregates rows into edges + a timeline; the history path emits one
/// pulse per message so the portal's rewind mode can scrub through the
/// activity message by message.
/// </para>
/// </remarks>
public class InteractionsQueryService(
    SpringDbContext dbContext,
    IParticipantDisplayNameResolver participantResolver) : IInteractionsQueryService
{
    /// <inheritdoc />
    public async Task<InteractionsGraph> GetAsync(
        InteractionsQueryFilters filters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var rows = await LoadDomainRowsAsync(
            filters.Since, filters.Until, cancellationToken);

        if (rows.Count == 0)
        {
            return new InteractionsGraph(
                Nodes: Array.Empty<InteractionsNode>(),
                Edges: Array.Empty<InteractionsEdge>(),
                Timeline: Array.Empty<InteractionsTimelineBucket>(),
                Truncated: null);
        }

        rows = ApplyScope(rows, filters.Unit, filters.Participant, filters.Neighbours);

        if (rows.Count == 0)
        {
            return new InteractionsGraph(
                Nodes: Array.Empty<InteractionsNode>(),
                Edges: Array.Empty<InteractionsEdge>(),
                Timeline: Array.Empty<InteractionsTimelineBucket>(),
                Truncated: null);
        }

        var nodeMap = BuildNodeMap(rows);
        var edgeMap = BuildEdgeMap(rows);

        // Apply the top-cap truncation. When the cap is set and the
        // unfiltered node count exceeds it, keep the top-N by
        // (Sent + Received) and drop edges that reference dropped nodes.
        // The truncation payload carries the original total so the portal
        // can render the "N of M" badge without re-fetching.
        var truncated = ApplyTopCap(nodeMap, edgeMap, filters.Cap);

        var nodes = await ResolveNodesAsync(nodeMap, cancellationToken);
        var edges = MaterializeEdges(edgeMap);

        var timeline = BuildTimeline(rows, filters.Bucket, filters.Since, filters.Until);

        // Order nodes / edges deterministically so consecutive snapshots
        // diff cleanly in the UI and integration tests aren't flaky on
        // hash-order. Nodes by (sent+received) desc then id asc; edges by
        // (count desc, fromId asc, toId asc).
        var orderedNodes = nodes
            .OrderByDescending(n => n.Sent + n.Received)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .ToList();
        var orderedEdges = edges
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.FromId, StringComparer.Ordinal)
            .ThenBy(e => e.ToId, StringComparer.Ordinal)
            .ToList();

        return new InteractionsGraph(orderedNodes, orderedEdges, timeline, truncated);
    }

    /// <inheritdoc />
    public async Task<InteractionsHistory> GetHistoryAsync(
        InteractionsHistoryFilters filters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);

        // The pulse stream needs the per-message id + thread id, so we
        // load a slightly wider projection than the snapshot path — same
        // table, same tenant filter, same window, plus messageId + threadId.
        // The snapshot's narrower projection is fine as a separate helper
        // (LoadDomainRowsAsync); we keep them distinct rather than forcing
        // every snapshot read to drag two extra Guids around.
        var pulseRows = await LoadDomainPulseRowsAsync(
            filters.Since, filters.Until, cancellationToken);

        if (pulseRows.Count == 0)
        {
            return new InteractionsHistory(
                Nodes: Array.Empty<InteractionsNode>(),
                Edges: Array.Empty<InteractionsEdge>(),
                Pulses: Array.Empty<InteractionsPulse>(),
                Truncated: null);
        }

        // Scope by neighbours, sharing the snapshot path's helper. The
        // wider pulse row carries the same (scheme, id) pair the
        // snapshot row does, so the row-typed helper accepts both shapes
        // via the IInteractionEndpoint interface.
        pulseRows = ApplyScopePulse(pulseRows, filters.Unit, filters.Participant, filters.Neighbours);

        if (pulseRows.Count == 0)
        {
            return new InteractionsHistory(
                Nodes: Array.Empty<InteractionsNode>(),
                Edges: Array.Empty<InteractionsEdge>(),
                Pulses: Array.Empty<InteractionsPulse>(),
                Truncated: null);
        }

        // Build node + edge aggregates from the same rows so the nodes /
        // edges block reflects exactly the messages we report as pulses.
        // (If we built nodes/edges from a separate query, a pulse-window
        // change would silently desynchronise the two blocks.)
        var snapshotRows = pulseRows.Select(r => r.AsRow()).ToList();
        var nodeMap = BuildNodeMap(snapshotRows);
        var edgeMap = BuildEdgeMap(snapshotRows);

        // Apply node-level cap, just like the snapshot path. The cap
        // counts nodes, not pulses; the two budgets are independent.
        var nodeTruncation = ApplyTopCap(nodeMap, edgeMap, filters.Cap);

        // Drop pulses whose endpoints did not survive the node cap.
        // (When the cap fires, the snapshot path drops edges referencing
        // dropped endpoints; we mirror that here so the pulse stream
        // matches the nodes / edges block exactly.)
        if (nodeTruncation is not null)
        {
            var keptIds = new HashSet<Guid>(nodeMap.Keys);
            pulseRows = pulseRows
                .Where(r => keptIds.Contains(r.SenderId) && keptIds.Contains(r.RecipientId))
                .ToList();
        }

        // Order pulses ascending by timestamp; tie-break by canonical
        // message id (no-dash 32-hex) so two messages that share a
        // millisecond produce a deterministic ordering across reads.
        var orderedPulses = pulseRows
            .OrderBy(r => r.SentAt)
            .ThenBy(r => GuidFormatter.Format(r.MessageId), StringComparer.Ordinal)
            .ToList();

        // Pulse-level truncation: drop the OLDEST when the budget overflows.
        // The brief: "drops the oldest pulses when exceeded (keeping the
        // most recent activity)". Rewind UX benefits more from "what just
        // happened" than from a complete prefix — when scrubbing backwards
        // through a busy window, the operator's first frame should still
        // be live activity.
        InteractionsPulseTruncation? pulseTruncation = null;
        var totalPulses = (long)orderedPulses.Count;
        if (filters.MaxPulses > 0 && orderedPulses.Count > filters.MaxPulses)
        {
            var keep = filters.MaxPulses;
            // Skip the prefix; keep the suffix (most recent).
            orderedPulses = orderedPulses
                .Skip(orderedPulses.Count - keep)
                .ToList();
            pulseTruncation = new InteractionsPulseTruncation(
                Total: totalPulses,
                Kept: orderedPulses.Count);
        }

        var nodes = await ResolveNodesAsync(nodeMap, cancellationToken);
        var edges = MaterializeEdges(edgeMap);

        var orderedNodes = nodes
            .OrderByDescending(n => n.Sent + n.Received)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .ToList();
        var orderedEdges = edges
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.FromId, StringComparer.Ordinal)
            .ThenBy(e => e.ToId, StringComparer.Ordinal)
            .ToList();

        var pulses = orderedPulses
            .Select(r => new InteractionsPulse(
                Id: GuidFormatter.Format(r.MessageId),
                FromId: GuidFormatter.Format(r.SenderId),
                ToId: GuidFormatter.Format(r.RecipientId),
                Timestamp: r.SentAt,
                ThreadId: r.ThreadId == Guid.Empty ? null : GuidFormatter.Format(r.ThreadId),
                Channel: r.RecipientScheme.ToLowerInvariant()))
            .ToList();

        // Truncation envelope: present when either branch fired. When
        // only the pulse branch fired, total == kept on the node side
        // (no nodes were actually dropped) so the envelope still
        // round-trips a coherent node total without misreporting.
        InteractionsHistoryTruncation? truncated = null;
        if (nodeTruncation is not null || pulseTruncation is not null)
        {
            var nodeTotal = nodeTruncation?.Total ?? (long)nodeMap.Count;
            var nodeKept = nodeTruncation?.Kept ?? (long)nodeMap.Count;
            truncated = new InteractionsHistoryTruncation(
                Total: nodeTotal,
                Kept: nodeKept,
                Pulses: pulseTruncation);
        }

        return new InteractionsHistory(orderedNodes, orderedEdges, pulses, truncated);
    }

    private async Task<List<InteractionRow>> LoadDomainRowsAsync(
        DateTimeOffset since, DateTimeOffset until, CancellationToken ct)
    {
        // Pull every Domain-class message in the window. We only need a
        // narrow projection — id + (scheme, id) pair for both ends + the
        // timestamp; payload + body never leave the database. The composite
        // index (tenant_id, thread_id, sent_at) on `messages` covers this
        // scan after EF applies the tenant filter; the time predicate is
        // a sargable range that PostgreSQL will use.
        //
        // The MessageType filter narrows to `Domain` so control envelopes
        // (Cancel, HealthCheck, StatusQuery, …) do not bloat the graph —
        // they are runtime-only per ADR-0030 and persist as a debugging
        // signal, not a routing event the visualization should surface.
        var messageType = nameof(MessageType.Domain);
        var rows = await dbContext.Messages
            .AsNoTracking()
            .Where(m => m.SentAt >= since
                     && m.SentAt < until
                     && m.MessageType == messageType)
            .Select(m => new InteractionRow(
                m.SenderScheme,
                m.SenderId,
                m.RecipientScheme,
                m.RecipientId,
                m.SentAt))
            .ToListAsync(ct);

        // Apply ADR-0048 defensively: drop any row whose recipient is a
        // connector. The router rejects these on write, so the filter is a
        // belt-and-suspenders check that keeps the test invariant honest
        // (a future migration that backfills legacy connector-to rows
        // never sneaks one onto the graph).
        return rows
            .Where(r => !string.Equals(r.RecipientScheme, Address.ConnectorScheme, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<List<InteractionPulseRow>> LoadDomainPulseRowsAsync(
        DateTimeOffset since, DateTimeOffset until, CancellationToken ct)
    {
        // Same time + type filter as LoadDomainRowsAsync, plus we project
        // the message id + thread id so the history endpoint can stamp
        // one pulse per individual message.
        var messageType = nameof(MessageType.Domain);
        var rows = await dbContext.Messages
            .AsNoTracking()
            .Where(m => m.SentAt >= since
                     && m.SentAt < until
                     && m.MessageType == messageType)
            .Select(m => new InteractionPulseRow(
                m.Id,
                m.ThreadId,
                m.SenderScheme,
                m.SenderId,
                m.RecipientScheme,
                m.RecipientId,
                m.SentAt))
            .ToListAsync(ct);

        return rows
            .Where(r => !string.Equals(r.RecipientScheme, Address.ConnectorScheme, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<InteractionRow> ApplyScope(
        List<InteractionRow> rows, Guid? unit, Guid? participant, int hops)
    {
        // Apply the unit / participant + neighbours scope by walking
        // outward from the focus id. The two filters are interchangeable
        // — both narrow to "edges touching id X plus N hops around" —
        // so when both are present we union the focus set.
        var focusIds = new HashSet<Guid>();
        if (unit is { } unitId) focusIds.Add(unitId);
        if (participant is { } participantId) focusIds.Add(participantId);

        if (focusIds.Count == 0)
        {
            return rows;
        }
        return ApplyNeighboursFilter(rows, focusIds, Math.Max(0, hops));
    }

    private static List<InteractionPulseRow> ApplyScopePulse(
        List<InteractionPulseRow> rows, Guid? unit, Guid? participant, int hops)
    {
        var focusIds = new HashSet<Guid>();
        if (unit is { } unitId) focusIds.Add(unitId);
        if (participant is { } participantId) focusIds.Add(participantId);

        if (focusIds.Count == 0)
        {
            return rows;
        }
        return ApplyNeighboursFilterPulse(rows, focusIds, Math.Max(0, hops));
    }

    private static Dictionary<Guid, NodeAggregate> BuildNodeMap(IEnumerable<InteractionRow> rows)
    {
        // Build the per-(scheme,id) node map. Sent/received counts come
        // from the row aggregates; the display name is resolved via the
        // injected resolver so the live entity tables are the source of
        // truth and renames never invalidate history.
        var nodeMap = new Dictionary<Guid, NodeAggregate>();
        foreach (var row in rows)
        {
            var sender = GetOrAdd(nodeMap, row.SenderId, row.SenderScheme);
            sender.Sent++;

            var recipient = GetOrAdd(nodeMap, row.RecipientId, row.RecipientScheme);
            recipient.Received++;
        }
        return nodeMap;
    }

    private static Dictionary<(Guid From, Guid To), EdgeAggregate> BuildEdgeMap(IEnumerable<InteractionRow> rows)
    {
        // Edge map: keyed on the (fromId, toId) tuple. Track count + first
        // / last timestamps + observed schemes for the snapshot / history.
        var edgeMap = new Dictionary<(Guid From, Guid To), EdgeAggregate>();
        foreach (var row in rows)
        {
            var key = (row.SenderId, row.RecipientId);
            if (!edgeMap.TryGetValue(key, out var agg))
            {
                agg = new EdgeAggregate(row.SentAt);
                edgeMap[key] = agg;
            }
            agg.Count++;
            if (row.SentAt < agg.FirstAt) agg.FirstAt = row.SentAt;
            if (row.SentAt > agg.LastAt) agg.LastAt = row.SentAt;
            agg.Channels.Add(row.RecipientScheme);
        }
        return edgeMap;
    }

    private static InteractionsTruncation? ApplyTopCap(
        Dictionary<Guid, NodeAggregate> nodeMap,
        Dictionary<(Guid From, Guid To), EdgeAggregate> edgeMap,
        int? cap)
    {
        if (cap is not { } capValue || nodeMap.Count <= capValue) return null;

        var keptIds = nodeMap
            .OrderByDescending(kv => kv.Value.Sent + kv.Value.Received)
            .ThenBy(kv => kv.Key)
            .Take(capValue)
            .Select(kv => kv.Key)
            .ToHashSet();

        var truncated = new InteractionsTruncation(
            Total: nodeMap.Count,
            Kept: keptIds.Count);

        // Drop nodes outside the kept set.
        foreach (var id in nodeMap.Keys.ToList())
        {
            if (!keptIds.Contains(id)) nodeMap.Remove(id);
        }
        // Drop edges referencing dropped endpoints.
        foreach (var key in edgeMap.Keys.ToList())
        {
            if (!keptIds.Contains(key.From) || !keptIds.Contains(key.To))
            {
                edgeMap.Remove(key);
            }
        }
        return truncated;
    }

    private async Task<List<InteractionsNode>> ResolveNodesAsync(
        Dictionary<Guid, NodeAggregate> nodeMap, CancellationToken ct)
    {
        // Resolve display names for the surviving nodes. The resolver
        // batches per-request via its own cache so repeated lookups for
        // the same address are cheap; an ad-hoc projection would have to
        // round-trip every entity table itself.
        var nodes = new List<InteractionsNode>(nodeMap.Count);
        foreach (var (id, agg) in nodeMap)
        {
            var address = new Address(agg.Scheme, id);
            var displayName = await participantResolver.ResolveAsync(address.ToString(), ct);
            nodes.Add(new InteractionsNode(
                Id: GuidFormatter.Format(id),
                Kind: agg.Scheme,
                DisplayName: displayName,
                Sent: agg.Sent,
                Received: agg.Received));
        }
        return nodes;
    }

    private static List<InteractionsEdge> MaterializeEdges(Dictionary<(Guid From, Guid To), EdgeAggregate> edgeMap) =>
        edgeMap
            .Select(kv => new InteractionsEdge(
                FromId: GuidFormatter.Format(kv.Key.From),
                ToId: GuidFormatter.Format(kv.Key.To),
                Count: kv.Value.Count,
                FirstAt: kv.Value.FirstAt,
                LastAt: kv.Value.LastAt,
                Channels: kv.Value.Channels.OrderBy(c => c, StringComparer.Ordinal).ToList()))
            .ToList();

    /// <summary>
    /// Narrow <paramref name="rows"/> to the edges within <paramref name="hops"/>
    /// of <paramref name="focusIds"/>. Hops are counted on the undirected
    /// graph: an edge between A and B is treated as a 1-hop neighbour
    /// relationship regardless of which direction the message travelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At <c>hops = 0</c> a row is in scope when either endpoint is a
    /// focus id — i.e. the direct edges of the focus node. At
    /// <c>hops = 1</c> the focus set expands to include every direct
    /// neighbour (any endpoint that shares an edge with a focus node);
    /// the filter then keeps rows whose endpoints are both in the
    /// expanded set. <c>hops = 2</c> applies the expansion twice. The
    /// default in the issue is <c>2</c>; deeper walks remain bounded
    /// because the <c>cap</c> default of <c>50</c> nodes already
    /// constrains the result set.
    /// </para>
    /// </remarks>
    private static List<InteractionRow> ApplyNeighboursFilter(
        List<InteractionRow> rows,
        HashSet<Guid> focusIds,
        int hops)
    {
        // hops = 0: keep every edge that touches a focus node (one of the
        // two endpoints is in the focus set). The "expand-then-restrict"
        // path below would over-narrow here — at hops = 0 we still want
        // the focus's direct edges to be visible.
        if (hops == 0)
        {
            return rows
                .Where(r => focusIds.Contains(r.SenderId) || focusIds.Contains(r.RecipientId))
                .ToList();
        }

        var inScope = new HashSet<Guid>(focusIds);
        for (var hop = 0; hop < hops; hop++)
        {
            var expanded = new HashSet<Guid>(inScope);
            foreach (var row in rows)
            {
                if (inScope.Contains(row.SenderId)) expanded.Add(row.RecipientId);
                if (inScope.Contains(row.RecipientId)) expanded.Add(row.SenderId);
            }
            if (expanded.Count == inScope.Count) break; // converged
            inScope = expanded;
        }

        return rows
            .Where(r => inScope.Contains(r.SenderId) && inScope.Contains(r.RecipientId))
            .ToList();
    }

    private static List<InteractionsTimelineBucket> BuildTimeline(
        List<InteractionRow> rows,
        InteractionsBucket bucketKind,
        DateTimeOffset since,
        DateTimeOffset until)
    {
        if (rows.Count == 0) return new List<InteractionsTimelineBucket>(0);

        // Compute bucket starts. UTC is the canonical timezone — every
        // surface stamps timestamps in UTC, so aligning here keeps the
        // boundary stable across hosts.
        var bucketRows = rows
            .Select(r => (Bucket: BucketStart(r.SentAt, bucketKind), Row: r))
            .ToList();

        var byBucket = bucketRows
            .GroupBy(t => t.Bucket)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Zero-fill empty buckets between the window's first and last
        // observed bucket so the portal can render a contiguous timeline
        // without having to walk gaps client-side. The lower / upper
        // bounds come from the window, not the data, so a quiet
        // sub-window still produces a coherent bar chart.
        var first = BucketStart(since, bucketKind);
        var last = BucketStart(until - TimeSpan.FromTicks(1), bucketKind);
        if (last < first) last = first;

        var buckets = new List<InteractionsTimelineBucket>();
        for (var b = first; b <= last; b = NextBucket(b, bucketKind))
        {
            if (!byBucket.TryGetValue(b, out var groupRows))
            {
                buckets.Add(new InteractionsTimelineBucket(
                    Bucket: b,
                    Sent: 0,
                    ByKind: EmptyByKind()));
                continue;
            }

            var sent = (long)groupRows.Count;
            var byKind = new Dictionary<string, long>
            {
                [Address.AgentScheme] = 0,
                [Address.UnitScheme] = 0,
                [Address.HumanScheme] = 0,
                [Address.ConnectorScheme] = 0,
            };
            foreach (var (_, row) in groupRows)
            {
                if (byKind.ContainsKey(row.SenderScheme))
                {
                    byKind[row.SenderScheme] += 1;
                }
            }

            buckets.Add(new InteractionsTimelineBucket(b, sent, byKind));
        }

        return buckets;
    }

    private static DateTimeOffset BucketStart(DateTimeOffset ts, InteractionsBucket kind)
    {
        var utc = ts.ToUniversalTime();
        return kind switch
        {
            InteractionsBucket.Day => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero),
            _ => new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero),
        };
    }

    private static DateTimeOffset NextBucket(DateTimeOffset b, InteractionsBucket kind) =>
        kind switch
        {
            InteractionsBucket.Day => b.AddDays(1),
            _ => b.AddHours(1),
        };

    private static IReadOnlyDictionary<string, long> EmptyByKind() => new Dictionary<string, long>
    {
        [Address.AgentScheme] = 0,
        [Address.UnitScheme] = 0,
        [Address.HumanScheme] = 0,
        [Address.ConnectorScheme] = 0,
    };

    private static NodeAggregate GetOrAdd(Dictionary<Guid, NodeAggregate> map, Guid id, string scheme)
    {
        if (!map.TryGetValue(id, out var agg))
        {
            // Lowercase the scheme so a row written in mixed case (legacy)
            // does not split the same actor into two nodes. The Address
            // constants are already lowercase; this is a defence-in-depth
            // normalisation.
            agg = new NodeAggregate(scheme.ToLowerInvariant());
            map[id] = agg;
        }
        return agg;
    }

    /// <summary>
    /// Wider variant of <see cref="InteractionRow"/> that carries the
    /// per-message identity (<see cref="MessageId"/>) and the thread id —
    /// the two extra columns the history endpoint stamps onto each pulse.
    /// </summary>
    private sealed record InteractionPulseRow(
        Guid MessageId,
        Guid ThreadId,
        string SenderScheme,
        Guid SenderId,
        string RecipientScheme,
        Guid RecipientId,
        DateTimeOffset SentAt)
    {
        public InteractionRow AsRow() => new(
            SenderScheme, SenderId, RecipientScheme, RecipientId, SentAt);
    }

    /// <summary>
    /// Pulse-row variant of <see cref="ApplyNeighboursFilter"/>. The two
    /// stay separate because the records are not co-variant — duplicating
    /// the BFS body is cheaper than introducing a row-typed abstraction
    /// for one extra row shape.
    /// </summary>
    private static List<InteractionPulseRow> ApplyNeighboursFilterPulse(
        List<InteractionPulseRow> rows,
        HashSet<Guid> focusIds,
        int hops)
    {
        if (hops == 0)
        {
            return rows
                .Where(r => focusIds.Contains(r.SenderId) || focusIds.Contains(r.RecipientId))
                .ToList();
        }

        var inScope = new HashSet<Guid>(focusIds);
        for (var hop = 0; hop < hops; hop++)
        {
            var expanded = new HashSet<Guid>(inScope);
            foreach (var row in rows)
            {
                if (inScope.Contains(row.SenderId)) expanded.Add(row.RecipientId);
                if (inScope.Contains(row.RecipientId)) expanded.Add(row.SenderId);
            }
            if (expanded.Count == inScope.Count) break;
            inScope = expanded;
        }

        return rows
            .Where(r => inScope.Contains(r.SenderId) && inScope.Contains(r.RecipientId))
            .ToList();
    }

    private sealed record InteractionRow(
        string SenderScheme,
        Guid SenderId,
        string RecipientScheme,
        Guid RecipientId,
        DateTimeOffset SentAt);

    private sealed class NodeAggregate(string scheme)
    {
        public string Scheme { get; } = scheme;
        public long Sent { get; set; }
        public long Received { get; set; }
    }

    private sealed class EdgeAggregate(DateTimeOffset first)
    {
        public long Count { get; set; }
        public DateTimeOffset FirstAt { get; set; } = first;
        public DateTimeOffset LastAt { get; set; } = first;
        public HashSet<string> Channels { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
