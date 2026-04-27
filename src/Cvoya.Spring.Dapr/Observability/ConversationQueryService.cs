// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IConversationQueryService"/>. Conversations are
/// materialised from the existing <see cref="SpringDbContext.ActivityEvents"/>
/// table: each activity event persisted with a non-null
/// <see cref="ActivityEventRecord.CorrelationId"/> carries the conversation id
/// assigned by the messaging layer. Grouping those events by correlation id
/// reconstructs the thread without a separate message store.
/// </summary>
/// <remarks>
/// This is an observability projection — it intentionally reads only from the
/// activity table, which is the single place the platform already persists
/// conversation-correlated events. A future PR can add a dedicated message
/// table (see #410) and swap this service out; every call site depends on the
/// interface only.
/// </remarks>
public class ConversationQueryService(SpringDbContext dbContext) : IConversationQueryService
{
    private static readonly string[] TerminalEventTypes =
    {
        nameof(ActivityEventType.ConversationCompleted),
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(
        ConversationQueryFilters filters,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.ActivityEvents
            .Where(e => e.CorrelationId != null)
            .Select(e => new ConversationEventRow(
                e.CorrelationId!, e.Id, e.Source, e.EventType, e.Severity, e.Summary, e.Timestamp))
            .ToListAsync(cancellationToken);

        var summaries = BuildSummaries(rows);
        summaries = ApplyFilters(summaries, filters);

        var limit = filters.Limit is > 0 ? filters.Limit.Value : 50;
        return summaries
            .OrderByDescending(s => s.LastActivity)
            .Take(limit)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ConversationDetail?> GetAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }

        var rows = await dbContext.ActivityEvents
            .Where(e => e.CorrelationId == conversationId)
            .OrderBy(e => e.Timestamp)
            .Select(e => new ConversationEventRow(
                e.CorrelationId!, e.Id, e.Source, e.EventType, e.Severity, e.Summary, e.Timestamp, e.Details))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return null;
        }

        var summary = BuildSummaryForConversation(conversationId, rows);
        var events = rows
            .Select(BuildConversationEvent)
            .ToList();

        return new ConversationDetail(summary, events);
    }

    /// <summary>
    /// Projects a single activity-event row into a <see cref="ConversationEvent"/>,
    /// pulling the message envelope (id / from / to / body) out of the
    /// <c>Details</c> JSON for <c>MessageReceived</c> events (#1209) so the
    /// conversation surfaces can render the body inline. Non-message events
    /// surface with the body fields null and the rest of the projection
    /// unchanged.
    /// </summary>
    private static ConversationEvent BuildConversationEvent(ConversationEventRow row)
    {
        var (messageId, from, to, body) = ExtractMessageEnvelope(row.Details);
        return new ConversationEvent(
            Id: row.Id,
            Timestamp: row.Timestamp,
            Source: NormaliseSource(row.Source),
            EventType: row.EventType,
            Severity: row.Severity,
            Summary: row.Summary,
            MessageId: messageId,
            From: from,
            To: to,
            Body: body);
    }

    /// <summary>
    /// Reads the message envelope fields written by
    /// <see cref="MessageReceivedDetails.Build"/>. Best-effort — a missing
    /// or malformed <c>Details</c> blob just leaves the projection fields
    /// null so older events (pre-#1209) still render correctly.
    /// </summary>
    private static (Guid? MessageId, string? From, string? To, string? Body) ExtractMessageEnvelope(JsonElement? details)
    {
        if (details is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null, null);
        }

        Guid? messageId = null;
        if (element.TryGetProperty(MessageReceivedDetails.MessageIdProperty, out var idProp)
            && idProp.ValueKind == JsonValueKind.String
            && Guid.TryParse(idProp.GetString(), out var parsedId))
        {
            messageId = parsedId;
        }

        var from = TryReadString(element, MessageReceivedDetails.FromProperty);
        var to = TryReadString(element, MessageReceivedDetails.ToProperty);
        var body = TryReadString(element, MessageReceivedDetails.BodyProperty);
        return (messageId, from, to, body);
    }

    private static string? TryReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop))
        {
            return null;
        }
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InboxItem>> ListInboxAsync(
        string humanAddress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(humanAddress))
        {
            return [];
        }

        // Normalise the caller's address — accept either "human://savasp" or
        // the persistence-layer shape "human:savasp" that activity events use.
        var humanSourceCanonical = ToPersistenceSource(humanAddress);
        var humanSourceDisplay = NormaliseSource(humanSourceCanonical);

        var rows = await dbContext.ActivityEvents
            .Where(e => e.CorrelationId != null)
            .Select(e => new ConversationEventRow(
                e.CorrelationId!, e.Id, e.Source, e.EventType, e.Severity, e.Summary, e.Timestamp))
            .ToListAsync(cancellationToken);

        var inbox = new List<InboxItem>();

        foreach (var group in rows.GroupBy(r => r.ConversationId))
        {
            // A conversation is "in my inbox" when the human has received a
            // domain message on it and has not replied since. The reply
            // signal is "another actor (non-human) emitted a MessageReceived
            // on the same conversation AFTER the human's last
            // MessageReceived" — that's the only path through which an
            // agent / unit observes the human's follow-up. #1210: keying
            // off "the LAST event must be the human's MessageReceived" was
            // too narrow — trailing observability events on the same
            // conversation (StateChanged on dispatch teardown, CostIncurred
            // from a budget enforcer, future event types added by extension
            // plugins) hid fresh agent replies even though the human had
            // genuinely not responded. Keying off the most recent
            // MessageReceived per side instead is robust to those tails.
            var ordered = group.OrderBy(r => r.Timestamp).ToList();

            ConversationEventRow? humanReceive = null;
            ConversationEventRow? laterAgentReceive = null;

            for (var i = ordered.Count - 1; i >= 0; i--)
            {
                var row = ordered[i];
                if (!string.Equals(row.EventType, nameof(ActivityEventType.MessageReceived), StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(row.Source, humanSourceCanonical, StringComparison.OrdinalIgnoreCase))
                {
                    humanReceive = row;
                    break;
                }

                if (laterAgentReceive is null
                    && !row.Source.StartsWith("human:", StringComparison.OrdinalIgnoreCase))
                {
                    // Track the latest non-human MessageReceived; if it
                    // comes after the human's, the human has already
                    // replied and the conversation is no longer pending.
                    laterAgentReceive = row;
                }
            }

            if (humanReceive is null)
            {
                continue;
            }

            if (laterAgentReceive is not null && laterAgentReceive.Timestamp > humanReceive.Timestamp)
            {
                continue;
            }

            // Find the "from" — the most recent non-human source before the
            // human's ask. Falls back to the human's own source when no
            // upstream actor is present (a synthetic conversation seeded
            // directly against the human address).
            var humanIndex = ordered.IndexOf(humanReceive);
            var from = ordered
                .Take(humanIndex)
                .Where(r => !r.Source.StartsWith("human:", StringComparison.OrdinalIgnoreCase))
                .Select(r => NormaliseSource(r.Source))
                .LastOrDefault() ?? NormaliseSource(humanReceive.Source);

            inbox.Add(new InboxItem(
                ConversationId: group.Key,
                From: from,
                Human: humanSourceDisplay,
                PendingSince: humanReceive.Timestamp,
                Summary: humanReceive.Summary));
        }

        return inbox
            .OrderByDescending(i => i.PendingSince)
            .ToList();
    }

    private static IReadOnlyList<ConversationSummary> BuildSummaries(List<ConversationEventRow> rows)
    {
        var summaries = new List<ConversationSummary>();
        foreach (var group in rows.GroupBy(r => r.ConversationId))
        {
            summaries.Add(BuildSummaryForConversation(group.Key, group.OrderBy(r => r.Timestamp).ToList()));
        }
        return summaries;
    }

    private static ConversationSummary BuildSummaryForConversation(
        string conversationId,
        IReadOnlyList<ConversationEventRow> ordered)
    {
        var first = ordered[0];
        var last = ordered[^1];
        var isCompleted = ordered.Any(r =>
            TerminalEventTypes.Contains(r.EventType));

        var participants = ordered
            .Select(r => NormaliseSource(r.Source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ConversationSummary(
            Id: conversationId,
            Participants: participants,
            Status: isCompleted ? "completed" : "active",
            LastActivity: last.Timestamp,
            CreatedAt: first.Timestamp,
            EventCount: ordered.Count,
            Origin: NormaliseSource(first.Source),
            Summary: first.Summary);
    }

    private static IReadOnlyList<ConversationSummary> ApplyFilters(
        IReadOnlyList<ConversationSummary> summaries,
        ConversationQueryFilters filters)
    {
        IEnumerable<ConversationSummary> query = summaries;

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            query = query.Where(s =>
                string.Equals(s.Status, filters.Status, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filters.Unit))
        {
            var needle = $"unit://{filters.Unit}";
            query = query.Where(s =>
                s.Participants.Any(p => string.Equals(p, needle, StringComparison.OrdinalIgnoreCase))
                || string.Equals(s.Origin, needle, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filters.Agent))
        {
            var needle = $"agent://{filters.Agent}";
            query = query.Where(s =>
                s.Participants.Any(p => string.Equals(p, needle, StringComparison.OrdinalIgnoreCase))
                || string.Equals(s.Origin, needle, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filters.Participant))
        {
            var needle = filters.Participant;
            query = query.Where(s =>
                s.Participants.Any(p => string.Equals(p, needle, StringComparison.OrdinalIgnoreCase)));
        }

        return query.ToList();
    }

    /// <summary>
    /// Turns the persistence-layer source format (<c>scheme:path</c>) into the
    /// wire-friendly <c>scheme://path</c> form. The activity mapper stores
    /// sources with a single colon to keep the column compact; the CLI and
    /// portal render the full URI.
    /// </summary>
    internal static string NormaliseSource(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return source;
        }

        var colon = source.IndexOf(':');
        if (colon < 0)
        {
            return source;
        }

        // Already in "scheme://path" form.
        if (source.AsSpan(colon + 1).StartsWith("//"))
        {
            return source;
        }

        var scheme = source[..colon];
        var path = source[(colon + 1)..];
        return $"{scheme}://{path}";
    }

    /// <summary>
    /// Accepts either <c>scheme://path</c> or <c>scheme:path</c> and returns
    /// the persistence-layer form (<c>scheme:path</c>) so we can compare
    /// against <see cref="ActivityEventRecord.Source"/> values.
    /// </summary>
    internal static string ToPersistenceSource(string address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return address;
        }

        var split = address.IndexOf("://", StringComparison.Ordinal);
        if (split < 0)
        {
            return address;
        }

        return string.Concat(address.AsSpan(0, split), ":", address.AsSpan(split + 3));
    }

    private sealed record ConversationEventRow(
        string ConversationId,
        Guid Id,
        string Source,
        string EventType,
        string Severity,
        string Summary,
        DateTimeOffset Timestamp,
        JsonElement? Details = null);
}