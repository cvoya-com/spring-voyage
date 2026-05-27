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
            .Where(m => m.SentAt >= filters.Since
                     && m.SentAt < filters.Until
                     && m.MessageType == messageType)
            .Select(m => new InteractionRow(
                m.SenderScheme,
                m.SenderId,
                m.RecipientScheme,
                m.RecipientId,
                m.SentAt))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return new InteractionsGraph(
                Nodes: Array.Empty<InteractionsNode>(),
                Edges: Array.Empty<InteractionsEdge>(),
                Timeline: Array.Empty<InteractionsTimelineBucket>(),
                Truncated: null);
        }

        // Apply ADR-0048 defensively: drop any row whose recipient is a
        // connector. The router rejects these on write, so the filter is a
        // belt-and-suspenders check that keeps the test invariant honest
        // (a future migration that backfills legacy connector-to rows
        // never sneaks one onto the graph).
        rows = rows
            .Where(r => !string.Equals(r.RecipientScheme, Address.ConnectorScheme, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (rows.Count == 0)
        {
            return new InteractionsGraph(
                Nodes: Array.Empty<InteractionsNode>(),
                Edges: Array.Empty<InteractionsEdge>(),
                Timeline: Array.Empty<InteractionsTimelineBucket>(),
                Truncated: null);
        }

        // Apply the unit / participant + neighbours scope by walking
        // outward from the focus id. The two filters are interchangeable
        // — both narrow to "edges touching id X plus N hops around" —
        // so when both are present we union the focus set.
        var focusIds = new HashSet<Guid>();
        if (filters.Unit is { } unitId) focusIds.Add(unitId);
        if (filters.Participant is { } participantId) focusIds.Add(participantId);

        if (focusIds.Count > 0)
        {
            rows = ApplyNeighboursFilter(rows, focusIds, Math.Max(0, filters.Neighbours));
        }

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

        // Edge map: keyed on the (fromId, toId) tuple. Track count + first
        // / last timestamps + observed schemes for the snapshot.
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

        // Apply the top-cap truncation. When the cap is set and the
        // unfiltered node count exceeds it, keep the top-N by
        // (Sent + Received) and drop edges that reference dropped nodes.
        // The truncation payload carries the original total so the portal
        // can render the "N of M" badge without re-fetching.
        InteractionsTruncation? truncated = null;
        if (filters.Cap is { } cap && nodeMap.Count > cap)
        {
            var keptIds = nodeMap
                .OrderByDescending(kv => kv.Value.Sent + kv.Value.Received)
                .ThenBy(kv => kv.Key)
                .Take(cap)
                .Select(kv => kv.Key)
                .ToHashSet();

            truncated = new InteractionsTruncation(
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
        }

        // Resolve display names for the surviving nodes. The resolver
        // batches per-request via its own cache so repeated lookups for
        // the same address are cheap; an ad-hoc projection would have to
        // round-trip every entity table itself.
        var nodes = new List<InteractionsNode>(nodeMap.Count);
        foreach (var (id, agg) in nodeMap)
        {
            var address = new Address(agg.Scheme, id);
            var displayName = await participantResolver.ResolveAsync(address.ToString(), cancellationToken);
            nodes.Add(new InteractionsNode(
                Id: GuidFormatter.Format(id),
                Kind: agg.Scheme,
                DisplayName: displayName,
                Sent: agg.Sent,
                Received: agg.Received));
        }

        var edges = edgeMap
            .Select(kv => new InteractionsEdge(
                FromId: GuidFormatter.Format(kv.Key.From),
                ToId: GuidFormatter.Format(kv.Key.To),
                Count: kv.Value.Count,
                FirstAt: kv.Value.FirstAt,
                LastAt: kv.Value.LastAt,
                Channels: kv.Value.Channels.OrderBy(c => c, StringComparer.Ordinal).ToList()))
            .ToList();

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
