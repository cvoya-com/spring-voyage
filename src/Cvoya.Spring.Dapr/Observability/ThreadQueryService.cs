// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

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
/// The directory service is consulted only to render an actor id back to its
/// scheme when a message references a recipient/sender that doesn't appear
/// in the thread's participant set (defensive — the dispatcher's invariants
/// keep these aligned in practice). Tenant scoping flows from
/// <see cref="SpringDbContext"/>'s <c>HasQueryFilter</c> on each entity, so
/// the service itself never references <c>OssTenantIds.Default</c> or
/// hardcodes a tenant string.
/// </para>
/// </remarks>
public class ThreadQueryService(
    SpringDbContext dbContext,
    IDirectoryService? directoryService = null) : IThreadQueryService
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
            summaries.Add(summary);
        }

        var filtered = await ApplyFiltersAsync(summaries, filters, cancellationToken);
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

        var events = messages.Select(BuildThreadEvent).ToList();
        return new ThreadDetail(summary, events);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InboxItem>> ListInboxAsync(
        string humanAddress,
        IReadOnlyDictionary<string, DateTimeOffset>? lastReadAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(humanAddress))
        {
            return [];
        }

        if (!Address.TryParse(ToParseable(humanAddress), out var parsed) || parsed is null)
        {
            return [];
        }

        var humanScheme = parsed.Scheme;
        var humanId = parsed.Id;

        // The inbox row predicate: a thread shows up when the human has
        // received at least one message on the thread and has not replied
        // since. Reduces to "MAX(sent_at WHERE recipient = me) >
        // MAX(sent_at WHERE sender = me)" per (tenant, thread). Any thread
        // where the human is not a recipient is excluded by the WHERE.
        var humanReceivedRows = await dbContext.Messages
            .AsNoTracking()
            .Where(m =>
                m.RecipientScheme == humanScheme
                && m.RecipientId == humanId)
            .GroupBy(m => m.ThreadId)
            .Select(g => new
            {
                ThreadId = g.Key,
                LastReceived = g.Max(m => m.SentAt),
            })
            .ToListAsync(cancellationToken);

        if (humanReceivedRows.Count == 0)
        {
            return [];
        }

        var threadIds = humanReceivedRows.Select(r => r.ThreadId).ToList();

        var humanSentRows = await dbContext.Messages
            .AsNoTracking()
            .Where(m =>
                threadIds.Contains(m.ThreadId)
                && m.SenderScheme == humanScheme
                && m.SenderId == humanId)
            .GroupBy(m => m.ThreadId)
            .Select(g => new
            {
                ThreadId = g.Key,
                LastSent = g.Max(m => m.SentAt),
            })
            .ToListAsync(cancellationToken);

        var humanSentByThread = humanSentRows.ToDictionary(r => r.ThreadId, r => r.LastSent);

        // Pending-since rows: the latest "to-the-human" message on each
        // thread, with its summary + sender. Single query covers the page.
        var pendingMessages = await dbContext.Messages
            .AsNoTracking()
            .Where(m =>
                threadIds.Contains(m.ThreadId)
                && m.RecipientScheme == humanScheme
                && m.RecipientId == humanId)
            .GroupBy(m => m.ThreadId)
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

        var humanRendered = RenderAddress(humanScheme, humanId);
        var inbox = new List<InboxItem>(humanReceivedRows.Count);

        foreach (var received in humanReceivedRows)
        {
            // Drop threads the human has already replied to.
            if (humanSentByThread.TryGetValue(received.ThreadId, out var lastSent)
                && lastSent >= received.LastReceived)
            {
                continue;
            }

            var pending = pendingMessages.FirstOrDefault(m => m.ThreadId == received.ThreadId);
            if (pending is null)
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
                Human: humanRendered,
                PendingSince: pending.SentAt,
                Summary: pending.Body ?? string.Empty,
                UnreadCount: unreadCount));
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

        // The thread's lifecycle status is owned by the threads row itself
        // (ADR-0040). Pre-rewrite the projection inferred completion from a
        // ThreadCompleted activity event; with the EF-authoritative model the
        // column is the source of truth. A separate writer must transition
        // this column when an agent emits ThreadCompleted/ThreadClosed; see
        // https://github.com/cvoya-com/spring-voyage/issues/2066.
        return new ThreadSummary(
            Id: GuidFormatter.Format(thread.Id),
            Participants: participants,
            Status: thread.Status,
            LastActivity: thread.LastActivityAt,
            CreatedAt: thread.CreatedAt,
            EventCount: messageCount,
            Origin: origin,
            Summary: summary);
    }

    private ThreadEvent BuildThreadEvent(MessageEntity message)
    {
        var source = RenderAddress(message.SenderScheme, message.SenderId);
        var to = RenderAddress(message.RecipientScheme, message.RecipientId);
        // Activity events surfaced "MessageReceived" to the timeline; we keep
        // the same event-type label so the wire shape downstream consumers
        // already render against (CLI rows, portal bubbles) is unchanged.
        return new ThreadEvent(
            Id: message.Id,
            Timestamp: message.SentAt,
            Source: source,
            EventType: "MessageReceived",
            Severity: "Info",
            Summary: message.Body ?? string.Empty,
            MessageId: message.Id,
            From: source,
            To: to,
            Body: message.Body);
    }

    private async Task<IReadOnlyList<ThreadSummary>> ApplyFiltersAsync(
        IReadOnlyList<ThreadSummary> summaries,
        ThreadQueryFilters filters,
        CancellationToken cancellationToken)
    {
        IEnumerable<ThreadSummary> query = summaries;

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            query = query.Where(s =>
                string.Equals(s.Status, filters.Status, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filters.Unit))
        {
            var needles = await BuildAddressNeedlesAsync("unit", filters.Unit!, cancellationToken);
            query = query.Where(s =>
                s.Participants.Any(p => needles.Contains(p, StringComparer.OrdinalIgnoreCase))
                || needles.Contains(s.Origin, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filters.Agent))
        {
            var needles = await BuildAddressNeedlesAsync("agent", filters.Agent!, cancellationToken);
            query = query.Where(s =>
                s.Participants.Any(p => needles.Contains(p, StringComparer.OrdinalIgnoreCase))
                || needles.Contains(s.Origin, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filters.Participant))
        {
            var needle = filters.Participant!;
            query = query.Where(s =>
                s.Participants.Any(p => string.Equals(p, needle, StringComparison.OrdinalIgnoreCase)));
        }

        return query.ToList();
    }

    /// <summary>
    /// Builds the candidate participant strings for an agent / unit slug-or-id
    /// filter. When the directory service resolves the value to a UUID, only
    /// the identity form (<c>scheme:id:&lt;uuid&gt;</c>) is returned so that
    /// threads from previous instances of an entity with the same slug name
    /// are not incorrectly included (#1488). The literal navigation form is
    /// returned as a fallback when: (a) no directory service is wired,
    /// (b) the value is already a UUID, or (c) resolution fails.
    /// </summary>
    private async Task<IReadOnlyList<string>> BuildAddressNeedlesAsync(
        string scheme,
        string value,
        CancellationToken cancellationToken)
    {
        var isUuidValue = Guid.TryParse(value, out _);
        var literal = isUuidValue
            ? $"{scheme}:id:{value}"
            : $"{scheme}://{value}";

        if (directoryService is null || !isUuidValue)
        {
            // The post-#1629 directory service is keyed by Guid only; a slug
            // needle that isn't already a Guid cannot resolve. Returning the
            // literal keeps direct-UUID filters working and lets slug filters
            // fall through with no match.
            return new[] { literal };
        }

        try
        {
            var entry = await directoryService.ResolveAsync(
                Address.For(scheme, value), cancellationToken);
            if (entry is null)
            {
                return new[] { literal };
            }

            return new[] { $"{scheme}:id:{GuidFormatter.Format(entry.ActorId)}" };
        }
        catch
        {
            return new[] { literal };
        }
    }

    /// <summary>
    /// Renders an actor (scheme + Guid id) as <c>scheme:id:&lt;hex&gt;</c> —
    /// the identity form the existing wire surface uses. The format matches
    /// what previously came out of <c>NormaliseSource</c> on
    /// <c>scheme:&lt;hex&gt;</c> activity rows.
    /// </summary>
    internal static string RenderAddress(string scheme, Guid id) =>
        $"{scheme}:id:{GuidFormatter.Format(id)}";

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
