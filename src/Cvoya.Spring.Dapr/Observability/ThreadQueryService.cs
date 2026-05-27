// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Threads;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IThreadQueryService"/>. Reads from the EF-authoritative
/// <c>threads</c> and <c>messages</c> tables landed by ADR-0030 and ADR-0040;
/// the legacy <c>activity_events.Details</c> JSON-scan path is gone.
/// </summary>
/// <remarks>
/// <para>
/// Thread identity comes from <see cref="ThreadEntity"/> (participant set,
/// <c>created_at</c>, <c>last_activity_at</c>, <c>status</c>) and the
/// timeline comes from <see cref="MessageEntity"/> ordered by
/// <c>sent_at</c>. The composite index <c>(tenant_id, thread_id, sent_at)</c>
/// on <c>messages</c> covers the per-thread message read; the registry's
/// <c>(tenant_id, participant_key)</c> unique index keeps the thread lookup
/// cheap.
/// </para>
/// <para>
/// Tenant scoping flows from <see cref="SpringDbContext"/>'s
/// <c>HasQueryFilter</c> on each entity, so the service itself never
/// references <c>OssTenantIds.Default</c> or hardcodes a tenant string.
/// </para>
/// </remarks>
public class ThreadQueryService(
    SpringDbContext dbContext,
    IParticipantDisplayNameResolver participantResolver) : IThreadQueryService
{
    private const int DefaultLimit = 50;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadSummary>> ListAsync(
        ThreadQueryFilters filters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var threads = await dbContext.Threads
            .AsNoTracking()
            .OrderByDescending(t => t.LastActivityAt)
            .ToListAsync(cancellationToken);

        if (threads.Count == 0)
        {
            return Array.Empty<ThreadSummary>();
        }

        var threadIds = threads.Select(t => t.Id).ToList();

        // Pull per-thread aggregates in two indexed queries: (count, first
        // sender id, first sender scheme, first body) for origin/summary and
        // (count) for the EventCount column. Both use the
        // (tenant_id, thread_id, sent_at) composite index.
        var counts = await dbContext.Messages
            .AsNoTracking()
            .Where(m => threadIds.Contains(m.ThreadId))
            .GroupBy(m => m.ThreadId)
            .Select(g => new { ThreadId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var firstMessages = await dbContext.Messages
            .AsNoTracking()
            .Where(m => threadIds.Contains(m.ThreadId))
            .GroupBy(m => m.ThreadId)
            .Select(g => g.OrderBy(m => m.SentAt).FirstOrDefault())
            .ToListAsync(cancellationToken);

        // ADR-0062 § 5 (#2826): resolve the recipient Hat per thread —
        // the Human id on the most recent message addressed to a human
        // recipient. Drives the portal's per-row Hat chip on the
        // engagement list and the unit / agent messaging-tab. The same
        // (tenant_id, thread_id, sent_at) composite index covers the
        // grouped scan; threads with no human-addressed message yield
        // no row and surface as `null` on the wire.
        var latestHumanRecipientByThread = await ResolveRecipientHumanIdsAsync(
            threadIds, cancellationToken);

        var countByThread = counts.ToDictionary(c => c.ThreadId, c => c.Count);
        var firstByThread = firstMessages
            .Where(m => m is not null)
            .Cast<MessageEntity>()
            .ToDictionary(m => m.ThreadId);

        var summaries = new List<ThreadSummary>(threads.Count);
        foreach (var thread in threads)
        {
            var summary = BuildSummary(
                thread,
                firstByThread.GetValueOrDefault(thread.Id),
                countByThread.GetValueOrDefault(thread.Id));
            // #2732: derive IsArchived from the participant set. The
            // resolver caches per-request, so repeated calls across a
            // hot list are cheap. We compute the flag before the in-
            // memory filter so the Archived predicate works against
            // the populated value.
            summary = summary with
            {
                IsArchived = await ComputeIsArchivedAsync(summary.Participants, cancellationToken),
                RecipientHumanId = latestHumanRecipientByThread.TryGetValue(thread.Id, out var humanId)
                    ? humanId
                    : null,
            };
            summaries.Add(summary);
        }

        var filtered = ApplyFilters(summaries, filters);
        var limit = filters.Limit is > 0 ? filters.Limit.Value : DefaultLimit;
        return filtered
            .OrderByDescending(s => s.LastActivity)
            .Take(limit)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ThreadDetail?> GetAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        if (!GuidFormatter.TryParse(threadId, out var threadGuid))
        {
            return null;
        }

        var thread = await dbContext.Threads
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == threadGuid, cancellationToken);

        if (thread is null)
        {
            return null;
        }

        var messages = await dbContext.Messages
            .AsNoTracking()
            .Where(m => m.ThreadId == threadGuid)
            .OrderBy(m => m.SentAt)
            .ToListAsync(cancellationToken);

        var summary = BuildSummary(
            thread,
            messages.Count > 0 ? messages[0] : null,
            messages.Count);

        // #2732: derive IsArchived once we know the canonical
        // participant set. GetAsync is the per-thread surface so a
        // single async pass is fine — there is no fan-out to amortise.
        // #2826: stamp the latest human-recipient on the summary so the
        // single-thread surface (`GET /api/v1/threads/{id}`) carries the
        // same Hat-chip data the list view does. The pick reuses the
        // already-fetched in-memory message list — no extra DB round-trip.
        summary = summary with
        {
            IsArchived = await ComputeIsArchivedAsync(summary.Participants, cancellationToken),
            RecipientHumanId = PickLatestHumanRecipient(messages),
        };

        var events = messages.Select(BuildThreadEvent).ToList();
        return new ThreadDetail(summary, events);
    }

    /// <summary>
    /// Resolves the latest human-recipient Hat per thread for a bulk
    /// list (#2826). Issues one grouped query against the messages
    /// table: per thread, pick the recipient id of the most recent
    /// message whose recipient scheme is <c>human:</c>. Threads with no
    /// human-addressed message do not appear in the result map; callers
    /// treat the absence as <c>null</c> on the wire.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, Guid>> ResolveRecipientHumanIdsAsync(
        IReadOnlyCollection<Guid> threadIds,
        CancellationToken cancellationToken)
    {
        if (threadIds.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        const string humanScheme = Address.HumanScheme;

        var latestHumanRecipients = await dbContext.Messages
            .AsNoTracking()
            .Where(m =>
                threadIds.Contains(m.ThreadId)
                && m.RecipientScheme == humanScheme)
            .GroupBy(m => m.ThreadId)
            .Select(g => g.OrderByDescending(m => m.SentAt).First())
            .ToListAsync(cancellationToken);

        return latestHumanRecipients.ToDictionary(m => m.ThreadId, m => m.RecipientId);
    }

    /// <summary>
    /// Picks the recipient id of the most recent message addressed to a
    /// <c>human:</c> recipient from an in-memory, ordered (oldest-first)
    /// message list. Returns <c>null</c> when no message in the list is
    /// addressed to a human (pure A2A threads). Used by
    /// <see cref="GetAsync"/> to avoid a second DB round-trip when the
    /// caller has already paid for the per-thread message scan.
    /// </summary>
    private static Guid? PickLatestHumanRecipient(IReadOnlyList<MessageEntity> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(
                messages[i].RecipientScheme,
                Address.HumanScheme,
                StringComparison.OrdinalIgnoreCase))
            {
                return messages[i].RecipientId;
            }
        }
        return null;
    }

    /// <summary>
    /// Derives the auto-archive flag (#2732) for a thread from its
    /// participant set. A thread is archived when it has at least one
    /// non-human participant AND every non-human participant is
    /// deleted. Solo-human threads return <c>false</c> (the "every non-
    /// human is deleted" predicate is vacuously true on an empty set,
    /// but the orphan UX requires at least one non-human to be present
    /// in the first place — otherwise there is nothing to be orphaned
    /// from). The lookup uses the resolver's per-request cache so
    /// repeated calls within the same scope (e.g. the same agent on
    /// multiple threads) round-trip the DB at most once.
    /// </summary>
    private async Task<bool> ComputeIsArchivedAsync(
        IReadOnlyList<string> participants,
        CancellationToken cancellationToken)
    {
        var nonHumanCount = 0;
        var deletedNonHumanCount = 0;

        foreach (var address in participants)
        {
            if (IsHumanAddress(address))
            {
                continue;
            }

            nonHumanCount++;
            if (await participantResolver.IsDeletedAsync(address, cancellationToken))
            {
                deletedNonHumanCount++;
            }
        }

        // Empty non-human set → not archived. Guards the solo-human
        // edge case (vacuous truth would otherwise mark these as
        // archived, which is wrong: there is no one to be orphaned
        // from).
        if (nonHumanCount == 0)
        {
            return false;
        }

        return deletedNonHumanCount == nonHumanCount;
    }

    /// <summary>
    /// Tests whether a canonical wire-form address belongs to a human
    /// participant. The orphan derivation excludes humans because the
    /// authenticated user is always live on their own engagements.
    /// </summary>
    private static bool IsHumanAddress(string address)
    {
        // Accept the canonical "human:<hex>" form plus the legacy
        // "human://path" / "human:id:<uuid>" forms for parity with
        // ParticipantDisplayNameResolver.
        var colonIdx = address.IndexOf(':');
        if (colonIdx <= 0)
        {
            return false;
        }

        var scheme = address[..colonIdx];
        return string.Equals(scheme, Address.HumanScheme, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadSearchHit>> SearchAsync(
        string participant,
        string query,
        string? threadId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(participant) || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ThreadSearchHit>();
        }

        if (!AddressIdentity.TryGetActorId(participant, out var participantId))
        {
            return Array.Empty<ThreadSearchHit>();
        }

        Guid? threadFilter = null;
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            if (!GuidFormatter.TryParse(threadId, out var parsedThreadId))
            {
                return Array.Empty<ThreadSearchHit>();
            }
            threadFilter = parsedThreadId;
        }

        // Resolve the threads the participant can see. The thread row
        // stores the participant set as a canonical JSON array, so the
        // membership test runs in C# after a bounded read — v0.1 thread
        // volumes per tenant make this cheap; a follow-up can add a
        // normalised thread_participants table when the volume grows.
        var allThreads = await dbContext.Threads
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var threadIds = new HashSet<Guid>();
        foreach (var thread in allThreads)
        {
            if (threadFilter is { } scoped && thread.Id != scoped)
            {
                continue;
            }
            foreach (var address in ParseParticipants(thread.Participants))
            {
                if (address.Id == participantId)
                {
                    threadIds.Add(thread.Id);
                    break;
                }
            }
        }

        if (threadIds.Count == 0)
        {
            return Array.Empty<ThreadSearchHit>();
        }

        var cappedLimit = Math.Max(1, limit);

        var baseQuery = dbContext.Messages
            .AsNoTracking()
            .Where(m => threadIds.Contains(m.ThreadId) && m.Body != null);

        List<MessageEntity> rows;
        var providerName = dbContext.Database.ProviderName ?? string.Empty;
        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            // Postgres path: lean on to_tsvector('english', body) /
            // plainto_tsquery so multi-word queries get sensible matching
            // and relevance ordering. No precomputed tsvector column /
            // GIN index today — v0.1 message volumes make this acceptable;
            // a follow-up issue can add the index when search latency
            // matters.
            rows = await baseQuery
                .Where(m => EF.Functions
                    .ToTsVector("english", m.Body!)
                    .Matches(EF.Functions.PlainToTsQuery("english", query)))
                .OrderByDescending(m => EF.Functions
                    .ToTsVector("english", m.Body!)
                    .Rank(EF.Functions.PlainToTsQuery("english", query)))
                .ThenByDescending(m => m.SentAt)
                .Take(cappedLimit)
                .ToListAsync(cancellationToken);
        }
        else
        {
            // Provider fallback (EF in-memory used in unit tests): plain
            // case-insensitive substring match on Body, newest first.
            var needle = query.Trim().ToLowerInvariant();
            rows = await baseQuery
                .Where(m => m.Body!.ToLower().Contains(needle))
                .OrderByDescending(m => m.SentAt)
                .Take(cappedLimit)
                .ToListAsync(cancellationToken);
        }

        var hits = new List<ThreadSearchHit>(rows.Count);
        foreach (var m in rows)
        {
            hits.Add(new ThreadSearchHit(
                ThreadId: GuidFormatter.Format(m.ThreadId),
                MessageId: m.Id,
                Timestamp: m.SentAt,
                From: RenderAddress(m.SenderScheme, m.SenderId),
                To: RenderAddress(m.RecipientScheme, m.RecipientId),
                Body: m.Body ?? string.Empty));
        }
        return hits;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InboxItem>> ListInboxAsync(
        IReadOnlyCollection<Guid> humanIds,
        IReadOnlyDictionary<string, DateTimeOffset>? lastReadAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(humanIds);

        if (humanIds.Count == 0)
        {
            return [];
        }

        // De-dup ahead of the EF Contains() so the resulting IN clause
        // stays tight regardless of the resolver's emit shape.
        var humanIdSet = humanIds.Distinct().ToList();
        const string humanScheme = Address.HumanScheme;

        // The inbox row predicate: a thread shows up when one of the
        // mapped humans has received at least one message on the thread
        // and the same human has not replied since. Reduces to
        // "MAX(sent_at WHERE recipient ∈ humans) > MAX(sent_at WHERE
        // sender ∈ humans)" per (tenant, thread, human). Per-thread,
        // per-human grouping is what lets MarkReadAsync resolve back to
        // the specific HumanActor that received the message rather than
        // the caller's login Human (#2766).
        var humanReceivedRows = await dbContext.Messages
            .AsNoTracking()
            .Where(m =>
                m.RecipientScheme == humanScheme
                && humanIdSet.Contains(m.RecipientId))
            .GroupBy(m => new { m.ThreadId, m.RecipientId })
            .Select(g => new
            {
                ThreadId = g.Key.ThreadId,
                HumanId = g.Key.RecipientId,
                LastReceived = g.Max(m => m.SentAt),
            })
            .ToListAsync(cancellationToken);

        if (humanReceivedRows.Count == 0)
        {
            return [];
        }

        var threadIds = humanReceivedRows.Select(r => r.ThreadId).Distinct().ToList();

        var humanSentRows = await dbContext.Messages
            .AsNoTracking()
            .Where(m =>
                threadIds.Contains(m.ThreadId)
                && m.SenderScheme == humanScheme
                && humanIdSet.Contains(m.SenderId))
            .GroupBy(m => new { m.ThreadId, m.SenderId })
            .Select(g => new
            {
                ThreadId = g.Key.ThreadId,
                HumanId = g.Key.SenderId,
                LastSent = g.Max(m => m.SentAt),
            })
            .ToListAsync(cancellationToken);

        var humanSentByPair = humanSentRows.ToDictionary(
            r => (r.ThreadId, r.HumanId),
            r => r.LastSent);

        // Pending-since rows: the latest "to-any-mapped-human" message on
        // each thread, with its summary + sender. Keyed by (thread, human)
        // so we can pair each received row to its own pending message.
        var pendingByPair = await dbContext.Messages
            .AsNoTracking()
            .Where(m =>
                threadIds.Contains(m.ThreadId)
                && m.RecipientScheme == humanScheme
                && humanIdSet.Contains(m.RecipientId))
            .GroupBy(m => new { m.ThreadId, m.RecipientId })
            .Select(g => g.OrderByDescending(m => m.SentAt).First())
            .ToListAsync(cancellationToken);

        // Per-thread message count for unread computation when no read
        // cursor is supplied — the legacy projection counted activity events,
        // we count messages.
        var messageCounts = await dbContext.Messages
            .AsNoTracking()
            .Where(m => threadIds.Contains(m.ThreadId))
            .GroupBy(m => m.ThreadId)
            .Select(g => new
            {
                ThreadId = g.Key,
                Count = g.Count(),
            })
            .ToListAsync(cancellationToken);

        var countByThread = messageCounts.ToDictionary(r => r.ThreadId, r => r.Count);

        // #2533: surface the participant-name snapshots stored on each
        // thread so the endpoint enrichment layer can prefer a captured
        // name over a per-scheme generic when the live row is gone.
        var threadSnapshots = await dbContext.Threads
            .AsNoTracking()
            .Where(t => threadIds.Contains(t.Id))
            .Select(t => new { t.Id, t.ParticipantNameSnapshots })
            .ToListAsync(cancellationToken);
        var snapshotsByThread = threadSnapshots.ToDictionary(
            t => t.Id,
            t => (IReadOnlyDictionary<string, string>)ParticipantNameSnapshotJson.Read(t.ParticipantNameSnapshots));

        // Bucket the latest pending-per-(thread,human) so the inbox row
        // construction is a single dictionary lookup.
        var pendingByLookup = pendingByPair.ToDictionary(
            m => (m.ThreadId, m.RecipientId),
            m => m);

        // Fan-in: when multiple mapped humans share a thread, emit one
        // inbox row per (thread, human) — the row's Human field
        // identifies which HumanActor's cursor MarkReadAsync should
        // advance. Display de-duplication happens client-side via the
        // engagement-id grouping; the per-human granularity is what
        // makes mark-read correct.
        var inbox = new List<InboxItem>(humanReceivedRows.Count);

        foreach (var received in humanReceivedRows)
        {
            // Drop threads where THIS human has already replied to the
            // last message they received.
            if (humanSentByPair.TryGetValue((received.ThreadId, received.HumanId), out var lastSent)
                && lastSent >= received.LastReceived)
            {
                continue;
            }

            if (!pendingByLookup.TryGetValue((received.ThreadId, received.HumanId), out var pending))
            {
                continue;
            }

            var threadIdString = GuidFormatter.Format(received.ThreadId);
            var unreadCount = ComputeUnreadCount(
                received.ThreadId,
                threadIdString,
                countByThread.GetValueOrDefault(received.ThreadId),
                lastReadAt,
                cancellationToken);

            inbox.Add(new InboxItem(
                ThreadId: threadIdString,
                From: RenderAddress(pending.SenderScheme, pending.SenderId),
                Human: RenderAddress(humanScheme, received.HumanId),
                PendingSince: pending.SentAt,
                Summary: pending.Body ?? string.Empty,
                UnreadCount: unreadCount,
                ParticipantNameSnapshots: snapshotsByThread.GetValueOrDefault(received.ThreadId)));
        }

        return inbox
            .OrderByDescending(i => i.PendingSince)
            .ToList();
    }

    private int ComputeUnreadCount(
        Guid threadGuid,
        string threadIdString,
        int totalMessages,
        IReadOnlyDictionary<string, DateTimeOffset>? lastReadAt,
        CancellationToken cancellationToken)
    {
        // No cursor → every message is unread; the existing surface treated
        // this as "count all events", and counting messages preserves the
        // semantics within the new shape.
        if (lastReadAt is null)
        {
            return totalMessages;
        }

        if (!lastReadAt.TryGetValue(threadIdString, out var cursor))
        {
            return totalMessages;
        }

        return dbContext.Messages
            .AsNoTracking()
            .Where(m => m.ThreadId == threadGuid && m.SentAt > cursor)
            .Count();
    }

    private ThreadSummary BuildSummary(
        ThreadEntity thread,
        MessageEntity? firstMessage,
        int messageCount)
    {
        IReadOnlyList<string> participants = ParseParticipants(thread.Participants)
            .Select(a => RenderAddress(a.Scheme, a.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var origin = firstMessage is not null
            ? RenderAddress(firstMessage.SenderScheme, firstMessage.SenderId)
            : participants.FirstOrDefault() ?? string.Empty;

        var summary = firstMessage?.Body
            ?? (firstMessage is not null ? string.Empty : string.Empty);

        // #2533: surface the per-participant display-name snapshot the
        // writer captured on every message arrival. The endpoint's
        // enrichment layer falls back to this when the live resolver
        // returns a per-scheme generic ("an agent", "a connector", …).
        var snapshots = ParticipantNameSnapshotJson.Read(thread.ParticipantNameSnapshots);

        // Per ADR-0030 a thread is a lifelong record — there is no thread-level
        // lifecycle status; the only state machine in the model is
        // per-(thread, participant). #2074 removed the legacy `status` column.
        return new ThreadSummary(
            Id: GuidFormatter.Format(thread.Id),
            Participants: participants,
            LastActivity: thread.LastActivityAt,
            CreatedAt: thread.CreatedAt,
            EventCount: messageCount,
            Origin: origin,
            Summary: summary,
            ParticipantNameSnapshots: snapshots);
    }

    private ThreadEvent BuildThreadEvent(MessageEntity message)
    {
        var source = RenderAddress(message.SenderScheme, message.SenderId);
        var to = RenderAddress(message.RecipientScheme, message.RecipientId);
        // Activity events surfaced "MessageArrived" to the timeline; we keep
        // the same event-type label so the wire shape downstream consumers
        // already render against (CLI rows, portal bubbles) is unchanged.
        return new ThreadEvent(
            Id: message.Id,
            Timestamp: message.SentAt,
            Source: source,
            EventType: "MessageArrived",
            Severity: "Info",
            Summary: message.Body ?? string.Empty,
            MessageId: message.Id,
            From: source,
            To: to,
            Body: message.Body);
    }

    private static IReadOnlyList<ThreadSummary> ApplyFilters(
        IReadOnlyList<ThreadSummary> summaries,
        ThreadQueryFilters filters)
    {
        IEnumerable<ThreadSummary> query = summaries;

        if (!string.IsNullOrWhiteSpace(filters.Unit))
        {
            var targetId = ResolveFilterIdentity(filters.Unit!);
            query = targetId is null
                ? Array.Empty<ThreadSummary>()
                : query.Where(s => MatchesActor(s, targetId.Value));
        }

        if (!string.IsNullOrWhiteSpace(filters.Agent))
        {
            var targetId = ResolveFilterIdentity(filters.Agent!);
            query = targetId is null
                ? Array.Empty<ThreadSummary>()
                : query.Where(s => MatchesActor(s, targetId.Value));
        }

        if (!string.IsNullOrWhiteSpace(filters.Participant))
        {
            // #2082: identity is a typed-Guid concept. Tolerate any of the
            // historical address forms by extracting the Guid via the
            // shared parser, then compare on the typed primitive — no
            // case-insensitive string equality on rendered addresses.
            if (AddressIdentity.TryGetActorId(filters.Participant!, out var participantId))
            {
                query = query.Where(s => s.Participants.Any(p =>
                    AddressIdentity.TryGetActorId(p, out var pid) && pid == participantId));
            }
            else
            {
                query = Array.Empty<ThreadSummary>();
            }
        }

        // #2732: archive-state filter. Omitted (null) is treated as
        // "exclude archived" so the default engagement list stays
        // uncluttered. archived=true returns ONLY archived threads —
        // drives the portal's separate archive surface.
        query = (filters.Archived ?? false) switch
        {
            true => query.Where(s => s.IsArchived),
            false => query.Where(s => !s.IsArchived),
        };

        // #2790: "since" lower bound on LastActivity drives the
        // Conversations view date filter + the matching CLI flag. The
        // comparison is on the activity timestamp (not CreatedAt) so a
        // long-lived thread with recent traffic surfaces above an
        // old-but-quiet thread, matching the sort order.
        if (filters.Since is DateTimeOffset since)
        {
            query = query.Where(s => s.LastActivity >= since);
        }

        return query.ToList();
    }

    /// <summary>
    /// Tests whether the given thread summary involves the actor whose
    /// stable Guid id is <paramref name="targetId"/>, either as a
    /// participant or as the origin sender. Both checks parse the
    /// stored address string to a Guid via
    /// <see cref="AddressIdentity.TryGetActorId"/> so identity matching
    /// is independent of which historical form the address was
    /// persisted in.
    /// </summary>
    private static bool MatchesActor(ThreadSummary summary, Guid targetId)
    {
        if (AddressIdentity.TryGetActorId(summary.Origin, out var originId)
            && originId == targetId)
        {
            return true;
        }

        return summary.Participants.Any(p =>
            AddressIdentity.TryGetActorId(p, out var pid) && pid == targetId);
    }

    /// <summary>
    /// Resolves a unit-or-agent filter value to the actor's stable Guid
    /// identity. Accepts a raw Guid string (the post-#1629 default) or
    /// any of the rendered address forms (canonical, navigation,
    /// identity). Returns <c>null</c> when the value cannot be parsed
    /// to a Guid — the caller treats that as "no match". Slug-shaped
    /// legacy filter values are not supported post-#1629; the directory
    /// is keyed by Guid only.
    /// </summary>
    private static Guid? ResolveFilterIdentity(string value)
    {
        if (AddressIdentity.TryGetActorId(value, out var direct))
        {
            return direct;
        }

        if (Guid.TryParse(value, out var raw))
        {
            return raw;
        }

        return null;
    }

    /// <summary>
    /// Renders an actor (scheme + Guid id) in the canonical wire form —
    /// <c>scheme:&lt;32-hex-no-dash&gt;</c> — matching
    /// <see cref="Address.ToString"/>. Issue #2082: the previous
    /// <c>scheme:id:&lt;hex&gt;</c> rendering disagreed with the form
    /// emitted by <c>AuthEndpoints.GetCurrentUserAsync</c>, causing
    /// downstream string-equality identity checks to silently miss
    /// matches. Identity comparisons should use
    /// <see cref="AddressIdentity.TryGetActorId"/> on the typed Guid,
    /// not string equality — but emitting a single canonical form here
    /// removes the cross-source drift entirely.
    /// </summary>
    internal static string RenderAddress(string scheme, Guid id) =>
        $"{scheme}:{GuidFormatter.Format(id)}";

    /// <summary>
    /// Parses the JSON array of canonical addresses written by
    /// <see cref="Cvoya.Spring.Dapr.Threads.EfThreadRegistry"/>. A malformed
    /// payload (should never happen in normal operation, since the registry
    /// is the only writer) yields an empty list rather than an exception.
    /// </summary>
    private static IReadOnlyList<Address> ParseParticipants(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<Address>();
        }

        try
        {
            var raw = JsonSerializer.Deserialize<string[]>(json);
            if (raw is null)
            {
                return Array.Empty<Address>();
            }

            var addresses = new List<Address>(raw.Length);
            foreach (var entry in raw)
            {
                if (Address.TryParse(entry, out var address) && address is not null)
                {
                    addresses.Add(address);
                }
            }

            return addresses;
        }
        catch (JsonException)
        {
            return Array.Empty<Address>();
        }
    }

    /// <summary>
    /// Accepts <c>scheme://path</c>, <c>scheme:id:&lt;hex&gt;</c>, or
    /// <c>scheme:&lt;hex&gt;</c> and reduces to the
    /// <c>scheme:&lt;hex&gt;</c> form <see cref="Address.TryParse"/> accepts.
    /// </summary>
    internal static string ToParseable(string address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return address;
        }

        var idIdx = address.IndexOf(":id:", StringComparison.Ordinal);
        if (idIdx > 0)
        {
            var scheme = address[..idIdx];
            var idPart = address[(idIdx + 4)..];
            return $"{scheme}:{idPart}";
        }

        var split = address.IndexOf("://", StringComparison.Ordinal);
        if (split > 0)
        {
            return string.Concat(address.AsSpan(0, split), ":", address.AsSpan(split + 3));
        }

        return address;
    }
}
