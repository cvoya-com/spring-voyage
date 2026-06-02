// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Globalization;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// Maps the tenant-wide Interactions visualization endpoints (#2867) — a
/// graph snapshot of who talked to whom over a time window plus a hot SSE
/// stream of new messages for the live view. Sits under
/// <c>/api/v1/tenant/observation/</c> alongside the threads observation
/// surface (#2787) and is gated by the same
/// <see cref="Cvoya.Spring.Core.Security.PlatformRoles.TenantObserver"/>
/// policy at registration time.
/// </summary>
public static class InteractionsEndpoints
{
    /// <summary>Default window when neither <c>since</c> nor <c>until</c> is supplied.</summary>
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    /// <summary>Default <c>cap</c> when omitted.</summary>
    private const int DefaultCap = 50;

    /// <summary>Default <c>maxPulses</c> budget for the history endpoint when omitted.</summary>
    private const int DefaultMaxPulses = 5000;

    /// <summary>Default per-edge coalesce window for the SSE stream.</summary>
    private const int DefaultCoalesceMs = 250;

    /// <summary>Default per-stream rate ceiling, events per second.</summary>
    private const int DefaultMaxRate = 50;

    /// <summary>Capacity for the bounded subscription channel.</summary>
    private const int ChannelCapacity = 512;

    /// <summary>
    /// Registers the interactions endpoints on the supplied route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group for chaining.</returns>
    public static RouteGroupBuilder MapInteractionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/observation/interactions")
            .WithTags("Observation");

        group.MapGet("/", GetInteractionsAsync)
            .WithName("GetInteractions")
            .WithSummary("Get the tenant-wide interactions graph (nodes / edges / timeline) for a time window")
            .Produces<InteractionsGraphResponse>(StatusCodes.Status200OK);

        group.MapGet("/stream", StreamInteractionsAsync)
            .WithName("StreamInteractions")
            .WithSummary("Stream live interactions via SSE (pulse / node-added / edge-added / throttled frames)");

        group.MapGet("/history", GetInteractionsHistoryAsync)
            .WithName("GetInteractionsHistory")
            .WithSummary("Get the tenant-wide interactions history (nodes / edges / per-message pulses) for a time window")
            .Produces<InteractionsHistoryResponse>(StatusCodes.Status200OK);

        return group;
    }

    private static async Task<IResult> GetInteractionsAsync(
        [AsParameters] InteractionsQuery query,
        IInteractionsQueryService interactionsService,
        CancellationToken cancellationToken)
    {
        // Window defaults: 10 minutes ending at now. Callers can override
        // either bound independently; we don't reject inverted ranges,
        // we just produce an empty graph (until <= since yields no rows).
        var until = query.Until ?? DateTimeOffset.UtcNow;
        var since = query.Since ?? until - DefaultWindow;

        var unitGuid = TryParseScopeId(query.Unit);
        var participantGuid = TryParseScopeId(query.Participant);

        var neighbours = query.Neighbours switch
        {
            null => 2,
            < 0 => 0,
            > 2 => 2,
            { } n => n,
        };

        var bucket = ParseBucket(query.Bucket);
        var cap = ParseCap(query.Cap);

        var filters = new InteractionsQueryFilters(
            Since: since,
            Until: until,
            Unit: unitGuid,
            Participant: participantGuid,
            Neighbours: neighbours,
            Bucket: bucket,
            Cap: cap);

        var graph = await interactionsService.GetAsync(filters, cancellationToken);

        var response = new InteractionsGraphResponse(
            Nodes: graph.Nodes
                .Select(n => new InteractionsNodeResponse(n.Id, n.Kind, n.DisplayName, n.Sent, n.Received))
                .ToList(),
            Edges: graph.Edges
                .Select(e => new InteractionsEdgeResponse(e.FromId, e.ToId, e.Count, e.FirstAt, e.LastAt, e.Channels))
                .ToList(),
            Timeline: graph.Timeline
                .Select(b => new InteractionsTimelineBucketResponse(b.Bucket, b.Sent, b.ByKind, b.ByActor))
                .ToList(),
            Truncated: graph.Truncated is { } t
                ? new InteractionsTruncationResponse(t.Total, t.Kept)
                : null);

        return Results.Ok(response);
    }

    private static async Task<IResult> GetInteractionsHistoryAsync(
        [AsParameters] InteractionsHistoryQuery query,
        IInteractionsQueryService interactionsService,
        CancellationToken cancellationToken)
    {
        // Window defaults mirror the snapshot endpoint: 10 minutes ending
        // at now. The history endpoint is the rewind affordance; the
        // default window matches what the operator sees in the live view
        // so toggling rewind doesn't yank them to a different time slice.
        var until = query.Until ?? DateTimeOffset.UtcNow;
        var since = query.Since ?? until - DefaultWindow;

        var unitGuid = TryParseScopeId(query.Unit);
        var participantGuid = TryParseScopeId(query.Participant);

        var neighbours = query.Neighbours switch
        {
            null => 2,
            < 0 => 0,
            > 2 => 2,
            { } n => n,
        };

        var cap = ParseCap(query.Cap);
        var maxPulses = query.MaxPulses is { } mp && mp > 0 ? mp : DefaultMaxPulses;

        var filters = new InteractionsHistoryFilters(
            Since: since,
            Until: until,
            Unit: unitGuid,
            Participant: participantGuid,
            Neighbours: neighbours,
            Cap: cap,
            MaxPulses: maxPulses);

        var history = await interactionsService.GetHistoryAsync(filters, cancellationToken);

        var response = new InteractionsHistoryResponse(
            Nodes: history.Nodes
                .Select(n => new InteractionsNodeResponse(n.Id, n.Kind, n.DisplayName, n.Sent, n.Received))
                .ToList(),
            Edges: history.Edges
                .Select(e => new InteractionsEdgeResponse(e.FromId, e.ToId, e.Count, e.FirstAt, e.LastAt, e.Channels))
                .ToList(),
            Pulses: history.Pulses
                .Select(p => new InteractionsPulseResponse(p.Id, p.FromId, p.ToId, p.Timestamp, p.ThreadId, p.Channel))
                .ToList(),
            Truncated: history.Truncated is { } t
                ? new InteractionsHistoryTruncationResponse(
                    t.Total,
                    t.Kept,
                    t.Pulses is { } pt
                        ? new InteractionsPulseTruncationResponse(pt.Total, pt.Kept)
                        : null)
                : null);

        return Results.Ok(response);
    }

    private static async Task StreamInteractionsAsync(
        HttpContext httpContext,
        IActivityEventBus activityEventBus,
        IParticipantDisplayNameResolver participantResolver,
        ILoggerFactory loggerFactory,
        string? unit,
        int? neighbours,
        int? coalesceMs,
        int? maxRate,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.InteractionsEndpoints");

        // Parse scope + tunables with sane defaults. Out-of-range values
        // fall back to defaults rather than 400-ing — SSE clients don't
        // get a recoverable error path and the defaults are reasonable.
        var unitGuid = TryParseScopeId(unit);
        var hops = neighbours switch
        {
            null => 2,
            < 0 => 0,
            > 2 => 2,
            { } n => n,
        };
        var coalesceWindow = TimeSpan.FromMilliseconds(coalesceMs is > 0 ? coalesceMs.Value : DefaultCoalesceMs);
        var rateCap = maxRate is > 0 ? maxRate.Value : DefaultMaxRate;

        // Resume counter: honour Last-Event-ID by skipping any frame
        // whose id is less than or equal to the supplied value. The
        // contract is "we ship monotonic ids; on reconnect we won't
        // re-emit anything you already saw". We don't replay history —
        // the stream is hot, so the moment of reconnect is the moment
        // we start emitting new frames.
        var nextEventId = ParseLastEventId(httpContext.Request.Headers["Last-Event-ID"]);

        // Hot source observable: every MessageArrived event in the
        // tenant. We use MessageArrived (recipient-emitted) because it
        // carries the canonical (sender, recipient, threadId) tuple in
        // its Details payload — see MessageArrivedDetails. MessageSent
        // emissions have inconsistent shapes across emitters and are
        // unsuitable for relay.
        var source = activityEventBus.ActivityStream
            .Where(e => e.EventType == ActivityEventType.MessageArrived);

        // Extract the per-event observation tuple. Events whose details
        // shape is malformed are dropped — a relay frame is no use
        // without a parsable (from, to) pair, and a partial pulse would
        // confuse the visualization more than dropping the event.
        var observed = source
            .Select(TryExtractObservation)
            .Where(o => o is not null)
            .Select(o => o!);

        // Apply the scope filter: when ?unit=… is supplied, restrict to
        // events touching the focus id within the requested hop depth.
        // Hops are evaluated against the running edge set seen on this
        // subscription — a unit we haven't seen yet but is N hops away
        // through prior pulses becomes visible as those intermediate
        // edges appear.
        var seenEdges = new HashSet<(Guid From, Guid To)>();
        var seenNodes = new Dictionary<Guid, string>();
        var inScope = unitGuid is { } focus
            ? new HashSet<Guid> { focus }
            : new HashSet<Guid>();

        // Channel decouples the Rx producer from the HTTP writer. The
        // capacity is large enough to absorb a routine burst; DropOldest
        // prevents a chatty stream from blocking the producer thread.
        var channel = Channel.CreateBounded<StreamFrame>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        // Per-edge coalesce buffers. Each edge gets its own short-window
        // buffer; the buffer flushes on the timer tick rather than on
        // every event so a sustained burst on one edge does not produce
        // hundreds of pulses per second.
        var coalesceBuffers = new Dictionary<(Guid From, Guid To), CoalesceBuffer>();
        var coalesceLock = new object();

        // Rate-cap state: token bucket per second. Drops + dropped count
        // accumulate between throttled frames so the client gets one
        // "dropped X events at T" rollup per cap window, not one per
        // dropped event.
        var rateCapper = new RateCapper(rateCap);
        long droppedSinceLastReport = 0;
        DateTimeOffset lastDropTimestamp = default;

        using var subscription = observed.Subscribe(
            o =>
            {
                if (unitGuid is { } unitFocus)
                {
                    var endpointInScope =
                        inScope.Contains(o.From) ||
                        inScope.Contains(o.To);

                    if (!endpointInScope)
                    {
                        // Not yet in scope. Expand the in-scope set when
                        // hops > 0 by looking at the running edge set —
                        // an edge whose other endpoint is now in scope
                        // pulls this endpoint in once hops budget remains.
                        if (hops > 0)
                        {
                            ExpandScopeForEvent(o, inScope, seenEdges, hops);
                        }
                        if (!inScope.Contains(o.From) && !inScope.Contains(o.To))
                        {
                            return;
                        }
                    }
                    else
                    {
                        // Endpoint already in scope. Pull the other side
                        // in as a 1-hop neighbour so subsequent events
                        // that touch it pass the scope check.
                        if (hops > 0)
                        {
                            inScope.Add(o.From);
                            inScope.Add(o.To);
                        }
                    }
                }

                // Rate cap: try to acquire a token. When the bucket is
                // empty we record a drop; the timer below emits the
                // accumulated throttle frame.
                if (!rateCapper.TryAcquire(o.Timestamp))
                {
                    Interlocked.Increment(ref droppedSinceLastReport);
                    lastDropTimestamp = o.Timestamp;
                    return;
                }

                // Node + edge introduction frames go onto the wire
                // before the pulse that references them so the
                // visualization can allocate the node/edge geometry.
                if (!seenNodes.ContainsKey(o.From))
                {
                    seenNodes[o.From] = o.FromScheme;
                    EnqueueFrame(channel, ref nextEventId, InteractionsStreamEvents.NodeAdded,
                        new InteractionsNodeAddedFrame(
                            Id: GuidFormatter.Format(o.From),
                            Kind: o.FromScheme,
                            DisplayName: ResolveDisplayName(participantResolver, o.From, o.FromScheme, logger)));
                }
                if (!seenNodes.ContainsKey(o.To))
                {
                    seenNodes[o.To] = o.ToScheme;
                    EnqueueFrame(channel, ref nextEventId, InteractionsStreamEvents.NodeAdded,
                        new InteractionsNodeAddedFrame(
                            Id: GuidFormatter.Format(o.To),
                            Kind: o.ToScheme,
                            DisplayName: ResolveDisplayName(participantResolver, o.To, o.ToScheme, logger)));
                }

                (Guid From, Guid To) edgeKey = (o.From, o.To);
                if (seenEdges.Add(edgeKey))
                {
                    EnqueueFrame(channel, ref nextEventId, InteractionsStreamEvents.EdgeAdded,
                        new InteractionsEdgeAddedFrame(
                            FromId: GuidFormatter.Format(o.From),
                            ToId: GuidFormatter.Format(o.To)));
                }

                // Coalesce into the per-edge buffer. The flush timer
                // owns the actual pulse emission; we just accumulate.
                lock (coalesceLock)
                {
                    if (!coalesceBuffers.TryGetValue(edgeKey, out var buf))
                    {
                        buf = new CoalesceBuffer();
                        coalesceBuffers[edgeKey] = buf;
                    }
                    buf.Add(o);
                }
            },
            ex =>
            {
                logger.LogWarning(ex, "Interactions SSE source faulted.");
                channel.Writer.TryComplete(ex);
            },
            () => channel.Writer.TryComplete());

        // Per-edge flush timer. Runs at the coalesce-window cadence:
        // on every tick we emit a pulse for every edge that's seen at
        // least one event in the previous window. A dedicated timer
        // (rather than a per-edge timer) keeps the bookkeeping bounded
        // — a fan-out across 10K edges does not allocate 10K timers.
        using var flushTimer = new System.Threading.PeriodicTimer(coalesceWindow);
        var flushTask = Task.Run(async () =>
        {
            try
            {
                while (await flushTimer.WaitForNextTickAsync(cancellationToken))
                {
                    FlushCoalesce(channel, ref nextEventId, coalesceBuffers, coalesceLock);
                    var dropped = Interlocked.Exchange(ref droppedSinceLastReport, 0);
                    if (dropped > 0)
                    {
                        EnqueueFrame(channel, ref nextEventId, InteractionsStreamEvents.Throttled,
                            new InteractionsThrottledFrame(
                                Since: lastDropTimestamp == default ? DateTimeOffset.UtcNow : lastDropTimestamp,
                                Dropped: dropped));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected — cancellation token tripped during shutdown.
            }
        }, cancellationToken);

        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        await httpContext.Response.Body.FlushAsync(cancellationToken);

        try
        {
            // Drain the channel, emitting an SSE keepalive comment during idle
            // gaps so the stream doesn't get torn down at a proxy/client idle
            // timeout during quiet periods (#3006 finding H).
            while (await SseKeepAlive.WaitForDataOrKeepAliveAsync(
                channel.Reader, httpContext.Response, cancellationToken))
            {
                while (channel.Reader.TryRead(out var frame))
                {
                    if (frame.Id <= ParseLastEventId(httpContext.Request.Headers["Last-Event-ID"]))
                    {
                        // Resume: skip frames the client already saw. The
                        // header value is captured per-frame so a mid-stream
                        // reconnect (rare; the typical flow is a fresh
                        // request) honours the most recent value.
                        continue;
                    }

                    var payload = JsonSerializer.Serialize(frame.Data, SerializerOptions);
                    await httpContext.Response.WriteAsync($"id: {frame.Id.ToString(CultureInfo.InvariantCulture)}\n", cancellationToken);
                    await httpContext.Response.WriteAsync($"event: {frame.EventName}\n", cancellationToken);
                    await httpContext.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                    await httpContext.Response.Body.FlushAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — expected.
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        await flushTask;
    }

    private static long ParseLastEventId(Microsoft.Extensions.Primitives.StringValues raw)
    {
        // The Last-Event-ID header rides along on EventSource reconnects.
        // We parse it leniently — a non-numeric or missing value resumes
        // from 0 (i.e. nothing skipped). This is the EventSource spec's
        // own behaviour for ill-formed ids.
        var s = raw.ToString();
        if (string.IsNullOrEmpty(s)) return 0;
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static void EnqueueFrame(
        Channel<StreamFrame> channel,
        ref long nextEventId,
        string eventName,
        object payload)
    {
        var id = Interlocked.Increment(ref nextEventId);
        channel.Writer.TryWrite(new StreamFrame(id, eventName, payload));
    }

    private static void FlushCoalesce(
        Channel<StreamFrame> channel,
        ref long nextEventId,
        Dictionary<(Guid From, Guid To), CoalesceBuffer> buffers,
        object coalesceLock)
    {
        List<(Guid From, Guid To, CoalesceBuffer Buf)>? snapshot = null;
        lock (coalesceLock)
        {
            if (buffers.Count == 0) return;
            snapshot = new List<(Guid, Guid, CoalesceBuffer)>(buffers.Count);
            foreach (var kv in buffers)
            {
                if (kv.Value.MessageIds.Count > 0)
                {
                    snapshot.Add((kv.Key.From, kv.Key.To, kv.Value));
                }
            }
            buffers.Clear();
        }

        foreach (var (from, to, buf) in snapshot)
        {
            EnqueueFrame(channel, ref nextEventId, InteractionsStreamEvents.Pulse,
                new InteractionsPulseFrame(
                    MessageIds: buf.MessageIds.Select(g => GuidFormatter.Format(g)).ToList(),
                    FromId: GuidFormatter.Format(from),
                    ToId: GuidFormatter.Format(to),
                    Timestamp: buf.LastTimestamp,
                    ThreadId: buf.LastThreadId,
                    Channel: buf.LastChannel ?? string.Empty,
                    Count: buf.MessageIds.Count));
        }
    }

    private static void ExpandScopeForEvent(
        Observation o,
        HashSet<Guid> inScope,
        HashSet<(Guid From, Guid To)> seenEdges,
        int hops)
    {
        // Cheap BFS over the running edge set: any node we've seen
        // within `hops` of a focus node becomes in-scope. We rebuild
        // each pass (the running edge set is bounded by the rate cap)
        // rather than maintain a persistent adjacency list.
        if (hops == 0) return;

        var frontier = new HashSet<Guid>(inScope);
        for (var i = 0; i < hops; i++)
        {
            var expanded = new HashSet<Guid>(frontier);
            foreach (var edge in seenEdges)
            {
                if (frontier.Contains(edge.From)) expanded.Add(edge.To);
                if (frontier.Contains(edge.To)) expanded.Add(edge.From);
            }
            // Also consider the inbound event itself for the next hop.
            if (frontier.Contains(o.From)) expanded.Add(o.To);
            if (frontier.Contains(o.To)) expanded.Add(o.From);
            if (expanded.Count == frontier.Count) break;
            frontier = expanded;
        }

        foreach (var id in frontier)
        {
            inScope.Add(id);
        }
    }

    private static Guid? TryParseScopeId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        // Accept canonical address (scheme:hex), legacy URI form
        // (scheme://hex), or a bare Guid. The filter ignores the
        // scheme — narrowing applies to the id only — so callers can
        // copy-paste any of the three forms.
        if (Address.TryParse(input, out var address) && address is not null)
        {
            return address.Id;
        }
        if (GuidFormatter.TryParse(input, out var id))
        {
            return id;
        }
        return null;
    }

    private static InteractionsBucket ParseBucket(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return InteractionsBucket.Second15;
        return input.Trim().ToLowerInvariant() switch
        {
            "15s" or "second15" => InteractionsBucket.Second15,
            "30s" or "second30" => InteractionsBucket.Second30,
            "1m" or "minute" or "minute1" => InteractionsBucket.Minute,
            "5m" or "minute5" => InteractionsBucket.Minute5,
            "10m" or "minute10" => InteractionsBucket.Minute10,
            "15m" or "minute15" => InteractionsBucket.Minute15,
            "30m" or "minute30" => InteractionsBucket.Minute30,
            "hour" or "1h" => InteractionsBucket.Hour,
            "day" => InteractionsBucket.Day,
            _ => InteractionsBucket.Second15,
        };
    }

    /// <summary>
    /// Parses the <c>cap</c> query parameter. Accepts an integer or the
    /// literal <c>none</c> (case-insensitive). A null / empty value
    /// returns the default cap; any unparseable value also falls back to
    /// the default — SSE clients don't have a recoverable error path.
    /// </summary>
    private static int? ParseCap(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return DefaultCap;
        var trimmed = input.Trim();
        if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase)) return null;
        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : DefaultCap;
    }

    private static Observation? TryExtractObservation(ActivityEvent evt)
    {
        // Recipient is the event source — MessageArrived is emitted by
        // the actor that received the mailbox arrival. Sender lives in
        // the Details payload under "from", written by
        // MessageArrivedDetails.Build as `scheme://hex`.
        if (evt.Details is not { } detailsBox) return null;
        var details = detailsBox;
        if (details.ValueKind != JsonValueKind.Object) return null;

        if (!details.TryGetProperty("from", out var fromProp) || fromProp.ValueKind != JsonValueKind.String) return null;
        var fromRaw = fromProp.GetString();
        if (string.IsNullOrEmpty(fromRaw) || !Address.TryParse(fromRaw, out var fromAddress) || fromAddress is null) return null;

        // Drop connector-recipient events defensively (ADR-0048): the
        // router rejects these on the way out, so a stray row would be
        // a bug we want to surface rather than animate.
        if (string.Equals(evt.Source.Scheme, Address.ConnectorScheme, StringComparison.OrdinalIgnoreCase)) return null;

        // Pull the message id from the canonical Details key — same key
        // MessageArrivedDetails writes.
        string? messageId = null;
        if (details.TryGetProperty("messageId", out var midProp))
        {
            messageId = midProp.ValueKind == JsonValueKind.String ? midProp.GetString() : null;
        }
        if (string.IsNullOrEmpty(messageId)) return null;
        if (!GuidFormatter.TryParse(messageId, out var messageGuid)) return null;

        return new Observation(
            From: fromAddress.Id,
            FromScheme: fromAddress.Scheme.ToLowerInvariant(),
            To: evt.Source.Id,
            ToScheme: evt.Source.Scheme.ToLowerInvariant(),
            MessageId: messageGuid,
            ThreadId: evt.CorrelationId,
            Timestamp: evt.Timestamp);
    }

    private static string ResolveDisplayName(
        IParticipantDisplayNameResolver resolver,
        Guid id,
        string scheme,
        ILogger logger)
    {
        // Resolve synchronously by waiting on the ValueTask — the
        // resolver caches per-request and the SSE path is already on a
        // bounded background thread so blocking here for a single
        // dictionary lookup is acceptable.
        try
        {
            var address = new Address(scheme, id);
            var task = resolver.ResolveAsync(address.ToString()).AsTask();
            return task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Display-name resolution failed for {Scheme} {Id}; using fallback.", scheme, id);
            return scheme;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Wire-frame envelope: id (for Last-Event-ID resume), event name
    /// (SSE <c>event:</c> header), and the typed payload object.
    /// </summary>
    private sealed record StreamFrame(long Id, string EventName, object Data);

    /// <summary>
    /// Per-event observation extracted from a MessageArrived activity.
    /// </summary>
    private sealed record Observation(
        Guid From,
        string FromScheme,
        Guid To,
        string ToScheme,
        Guid MessageId,
        string? ThreadId,
        DateTimeOffset Timestamp);

    /// <summary>
    /// Per-edge coalesce buffer. Accumulates the message ids observed
    /// for an edge within the current coalesce window; flushed by the
    /// periodic timer.
    /// </summary>
    private sealed class CoalesceBuffer
    {
        public List<Guid> MessageIds { get; } = new();
        public DateTimeOffset LastTimestamp { get; private set; }
        public string? LastThreadId { get; private set; }
        public string? LastChannel { get; private set; }

        public void Add(Observation o)
        {
            MessageIds.Add(o.MessageId);
            LastTimestamp = o.Timestamp;
            LastThreadId = o.ThreadId;
            LastChannel = o.ToScheme;
        }
    }

    /// <summary>
    /// Simple token-bucket rate cap. The bucket refills at the start
    /// of each new wall-clock second; an empty bucket drops events
    /// rather than queuing them so a sustained burst translates into
    /// a single throttled frame per cap window instead of a backed-up
    /// channel.
    /// </summary>
    private sealed class RateCapper(int capacity)
    {
        private long _windowStart;
        private int _remaining;
        private readonly int _capacity = capacity;

        public bool TryAcquire(DateTimeOffset ts)
        {
            // Use the wall-clock second as the bucket window. Multiple
            // events with the same second tick share the bucket; the
            // first one to find an empty bucket gets dropped.
            var windowSecond = ts.ToUnixTimeSeconds();
            lock (this)
            {
                if (windowSecond != _windowStart)
                {
                    _windowStart = windowSecond;
                    _remaining = _capacity;
                }
                if (_remaining <= 0) return false;
                _remaining--;
                return true;
            }
        }
    }
}
