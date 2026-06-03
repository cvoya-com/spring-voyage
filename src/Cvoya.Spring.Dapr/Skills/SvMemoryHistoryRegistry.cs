// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Platform-level <see cref="ISkillRegistry"/> implementing the
/// shared-history surface of <c>sv.memory.*</c> (#2747). Lets a runtime
/// fetch the timeline it shares with a participant set, enumerate the
/// engagements (participant sets) it is part of, and search across those
/// shared timelines — all <i>without ever naming a <c>thread_id</c></i>.
/// The platform derives the thread id internally from the participant set
/// per ADR-0030.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replaces <c>sv.thread.*</c></b>. The previous
/// <c>sv.thread.{list,get,search,participants}</c> tools exposed
/// <c>thread_id</c> as a first-class input/output and required the
/// caller to discover it via inspection. Per #2747 the participant set
/// is the primitive: the agent passes a list of participants and the
/// platform handles the bookkeeping. <c>sv.thread.participants</c> is
/// retired entirely because the participants are the API input — there
/// is nothing for the tool to return that the caller didn't supply.
/// </para>
/// <para>
/// <b>Sender auto-include.</b> Every call canonicalises the participant
/// set as <c>{caller} ∪ supplied</c> before resolving the thread, so the
/// agent does not list itself in <c>participants</c>. A future flag (out
/// of scope for v0.1) may let a permitted caller suppress auto-include
/// to inspect a set it is not part of.
/// </para>
/// <para>
/// <b>Caller scope.</b> A caller can only inspect threads it participates
/// in. With sender auto-include, that invariant is enforced by construction
/// — the resolved thread always contains the caller — but the call also
/// double-checks against <see cref="ThreadRegistryEntry.Participants"/>
/// before returning timeline data.
/// </para>
/// <para>
/// <b>Connector participants.</b> A <c>connector://</c> address is a
/// legitimate member of a participant set (it stamps message provenance
/// on inbound webhook events) and may appear in <c>participants</c>.
/// Routing a message TO a connector is rejected by the messaging tools
/// (see <see cref="MessageDeliveryService.EnsureCanReceive"/>), but the
/// shared history is queryable.
/// </para>
/// </remarks>
public sealed class SvMemoryHistoryRegistry : ISkillRegistry
{
    /// <summary>Tool name for <c>sv.memory.engagements</c>.</summary>
    public const string EngagementsTool = "sv.memory.engagements";

    /// <summary>Tool name for <c>sv.memory.history_with</c>.</summary>
    public const string HistoryWithTool = "sv.memory.history_with";

    /// <summary>Tool name for <c>sv.memory.search_messages</c>.</summary>
    public const string SearchMessagesTool = "sv.memory.search_messages";

    /// <summary>Tool name for <c>sv.memory.get_messages</c>.</summary>
    public const string GetMessagesTool = "sv.memory.get_messages";

    /// <summary>Default page size for list / search tools.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Maximum allowed page size.</summary>
    public const int MaxLimit = 200;

    /// <summary>Maximum number of ids <c>sv.memory.get_messages</c> accepts per call.</summary>
    public const int MaxMessageIds = 100;

    private static readonly JsonElement EngagementsSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50, "description": "Maximum number of engagements to return (default 50, max 200)." }
          }
        }
        """);

    private static readonly JsonElement HistoryWithSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "participants": {
              "type": "array",
              "minItems": 1,
              "description": "Optional. The other participants on the shared timeline whose history you want; the calling participant is auto-included, so do not list yourself. Connectors (connector:<32-hex>) may appear here as the sender of an inbound webhook event. When omitted, defaults to the conversation you are currently in — calling this tool with no arguments re-reads the history of the conversation that prompted your turn.",
              "items": { "type": "string", "description": "Canonical scheme:no-dash-hex address." }
            },
            "tail": { "type": "integer", "minimum": 1, "maximum": 500, "description": "When supplied, return only the most-recent N messages on the shared timeline (default: every persisted message)." }
          }
        }
        """);

    private static readonly JsonElement SearchMessagesSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["query"],
          "properties": {
            "query": { "type": "string", "description": "Free-text query matched against persisted message bodies." },
            "participants": {
              "type": "array",
              "minItems": 1,
              "description": "Optional — scope the search to a single shared timeline. The calling participant is auto-included; do not list yourself. When omitted, the search spans every timeline the caller participates in.",
              "items": { "type": "string", "description": "Canonical scheme:no-dash-hex address." }
            },
            "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50, "description": "Maximum number of matching messages to return (default 50, max 200)." }
          }
        }
        """);

    private static readonly JsonElement GetMessagesSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["message_ids"],
          "properties": {
            "message_ids": {
              "type": "array",
              "minItems": 1,
              "maxItems": 100,
              "description": "The message ids to fetch (1–100), each the no-dash 32-char hex message_id you already hold from an inbound envelope. Ids you cannot read — unknown, or on a thread you do not participate in — come back under `skipped`, never as an error.",
              "items": { "type": "string", "description": "A message_id (no-dash 32-char hex)." }
            }
          }
        }
        """);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    /// <summary>Builds the registry with its scoped read dependencies.</summary>
    public SvMemoryHistoryRegistry(
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = loggerFactory.CreateLogger<SvMemoryHistoryRegistry>();

        _tools = new[]
        {
            new ToolDefinition(
                EngagementsTool,
                "List the participant sets (engagements) the calling agent or unit shares a timeline with, most-recent activity first. Each entry carries { participants, last_activity, created_at, event_count, origin, summary }. Pagination via limit (default 50, max 200). The participant set is the primitive — use sv.memory.history_with(participants=[…]) to fetch a specific engagement's timeline.",
                EngagementsSchema,
                ToolCategories.Memory),
            new ToolDefinition(
                HistoryWithTool,
                "Fetch the full message timeline shared with the supplied participants. The calling participant is auto-included; do not list yourself. Omit participants to default to the conversation you are currently in — calling it with no arguments re-reads this conversation's history, which is how you recover what was actually said instead of relying on memory. Returns { participants, messages: [{ message_id, timestamp, from, to, body }, ...] } ordered oldest first. Pass tail=N to return only the most-recent N messages. Returns null when no shared timeline exists yet for that participant set.",
                HistoryWithSchema,
                ToolCategories.Memory),
            new ToolDefinition(
                SearchMessagesTool,
                "Free-text search across persisted messages on the timelines the caller participates in. Optional participants list scopes the search to a single shared timeline (caller auto-included). Returns { hits: [{ participants, message_id, timestamp, from, to, body }, ...] } ordered by relevance (when the database supports full-text search) or by recency otherwise. limit defaults to 50 (max 200).",
                SearchMessagesSchema,
                ToolCategories.Memory),
            new ToolDefinition(
                GetMessagesTool,
                "Fetch specific messages by message_id (1–100 per call) — the by-id complement to sv.memory.history_with (a whole timeline) and sv.memory.search_messages (free-text). Use it when you already hold the ids, e.g. to re-read a message you were notified about. Returns { messages: [{ message_id, timestamp, from, to, body }, ...], skipped: [\"<message_id>\", ...] }, with messages in the order you asked for them. You may only read a message on a thread you participate in; any id that is unknown or that you cannot access comes back under skipped (the two cases are indistinguishable on purpose), never as an error.",
                GetMessagesSchema,
                ToolCategories.Memory),
        };
    }

    /// <inheritdoc />
    public string Name => "sv";

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools;

    /// <inheritdoc />
    public Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
        throw new SpringException(
            $"Tool '{toolName}' on the {Name} registry requires caller context. " +
            "It is reachable only through the caller-aware ISkillRegistry.InvokeAsync overload " +
            "(invoked by the MCP server with the active session's identity). Direct callers " +
            "must use the rich overload.");

    /// <inheritdoc />
    public Task<JsonElement> InvokeAsync(
        string toolName,
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        return toolName switch
        {
            EngagementsTool => EngagementsAsync(arguments, context, cancellationToken),
            HistoryWithTool => HistoryWithAsync(arguments, context, cancellationToken),
            SearchMessagesTool => SearchMessagesAsync(arguments, context, cancellationToken),
            GetMessagesTool => GetMessagesAsync(arguments, context, cancellationToken),
            _ => throw new SkillNotFoundException(toolName),
        };
    }

    private async Task<JsonElement> EngagementsAsync(
        JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var caller = ParseCaller(context);
        var limit = ParseLimit(args);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var queryService = scope.ServiceProvider.GetRequiredService<IThreadQueryService>();

        var filters = new ThreadQueryFilters(
            Participant: caller.ToString(),
            Limit: limit);
        var summaries = await queryService.ListAsync(filters, ct);

        _logger.LogDebug(
            "sv.memory.engagements caller={Caller} returned={Count}",
            caller, summaries.Count);

        return SerializeEngagements(summaries, limit);
    }

    private async Task<JsonElement> HistoryWithAsync(
        JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var caller = ParseCaller(context);
        var others = TryReadParticipantArray(args, "participants");
        var tail = TryReadIntArg(args, "tail");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var queryService = scope.ServiceProvider.GetRequiredService<IThreadQueryService>();

        string threadId;
        if (others is { Count: > 0 })
        {
            var registry = scope.ServiceProvider.GetRequiredService<IThreadRegistry>();
            var participants = BuildParticipantSet(caller, others);
            threadId = await registry.GetOrCreateAsync(participants, ct);
        }
        else if (GuidFormatter.TryParse(context.ThreadId, out var activeThread))
        {
            // Default (#3036): no participant set named → the conversation the
            // caller is currently in. context.ThreadId is the caller's active
            // thread, so this re-reads "this conversation" — the most common
            // intent, and the fumble that left agents blind in #3034.
            threadId = GuidFormatter.Format(activeThread);
        }
        else
        {
            throw new ArgumentException(
                "sv.memory.history_with needs a participant set to resolve a timeline. " +
                "Pass `participants` — the other addresses on the conversation (you are auto-included) — " +
                "or call this tool from within a conversation to default to the one you are in.");
        }

        var detail = await queryService.GetAsync(threadId, ct);
        if (detail is null || !ParticipatesIn(detail.Summary, caller))
        {
            return JsonNull();
        }

        var events = detail.Events;
        if (tail is { } take && take > 0 && events.Count > take)
        {
            events = events.Skip(events.Count - take).ToList();
        }

        return SerializeHistory(detail.Summary, events);
    }

    private async Task<JsonElement> SearchMessagesAsync(
        JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var caller = ParseCaller(context);
        var query = RequireStringArg(args, "query");
        var others = TryReadParticipantArray(args, "participants");
        var limit = ParseLimit(args);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var queryService = scope.ServiceProvider.GetRequiredService<IThreadQueryService>();

        string? threadId = null;
        if (others is { Count: > 0 })
        {
            var registry = scope.ServiceProvider.GetRequiredService<IThreadRegistry>();
            var participants = BuildParticipantSet(caller, others);
            threadId = await registry.GetOrCreateAsync(participants, ct);
        }

        var hits = await queryService.SearchAsync(caller.ToString(), query, threadId, limit, ct);
        return SerializeHits(hits, limit);
    }

    private async Task<JsonElement> GetMessagesAsync(
        JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var caller = ParseCaller(context);
        var ids = RequireMessageIdArray(args, "message_ids");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var queryService = scope.ServiceProvider.GetRequiredService<IThreadQueryService>();

        var lookup = await queryService.GetMessagesByIdsAsync(caller.ToString(), ids, ct);

        _logger.LogDebug(
            "sv.memory.get_messages caller={Caller} requested={Requested} returned={Returned} skipped={Skipped}",
            caller, ids.Count, lookup.Messages.Count, lookup.Skipped.Count);

        return SerializeMessages(lookup);
    }

    private static Address ParseCaller(ToolCallContext context)
    {
        if (context is null)
        {
            throw new SpringException("Tool call context is missing.");
        }
        if (string.IsNullOrWhiteSpace(context.CallerId))
        {
            throw new SpringException("Tool call context is missing the caller id.");
        }
        if (!GuidFormatter.TryParse(context.CallerId, out var guid))
        {
            throw new SpringException($"Caller id '{context.CallerId}' is not a parseable Guid.");
        }
        var scheme = string.IsNullOrWhiteSpace(context.CallerKind)
            ? Address.AgentScheme
            : context.CallerKind;
        return new Address(scheme, guid);
    }

    private static IReadOnlyList<Address> BuildParticipantSet(Address caller, IReadOnlyList<Address> others)
    {
        var set = new List<Address>(others.Count + 1) { caller };
        var callerKey = caller.ToString();
        foreach (var other in others)
        {
            if (!string.Equals(other.ToString(), callerKey, StringComparison.Ordinal))
            {
                set.Add(other);
            }
        }
        return set;
    }

    private static IReadOnlyList<Address>? TryReadParticipantArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var prop)
            || prop.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<Address>(prop.GetArrayLength());
        foreach (var element in prop.EnumerateArray())
        {
            var raw = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (!Address.TryParse(raw, out var address) || address is null)
            {
                throw new ArgumentException($"'{raw}' is not a valid Spring Voyage address.");
            }
            list.Add(address);
        }
        return list;
    }

    private static IReadOnlyList<string> RequireMessageIdArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var prop)
            || prop.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(
                $"sv.memory.get_messages requires `{name}`: a non-empty array (1–{MaxMessageIds}) of message_id " +
                "strings you already hold — each the no-dash 32-char hex `message_id` from an inbound envelope, " +
                "or from sv.memory.history_with / sv.memory.search_messages.");
        }

        var list = new List<string>(prop.GetArrayLength());
        foreach (var element in prop.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"Each entry in '{name}' must be a string message id.");
            }
            var raw = element.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ArgumentException($"Each entry in '{name}' must be a non-empty message id.");
            }
            list.Add(raw);
        }

        if (list.Count == 0)
        {
            throw new ArgumentException(
                $"`{name}` must contain at least one message_id — the no-dash 32-char hex id from an inbound " +
                "envelope, or from sv.memory.history_with / sv.memory.search_messages.");
        }
        if (list.Count > MaxMessageIds)
        {
            throw new ArgumentException(
                $"Argument '{name}' accepts at most {MaxMessageIds} message ids per call (received {list.Count}).");
        }
        return list;
    }

    private static bool ParticipatesIn(ThreadSummary summary, Address caller)
    {
        foreach (var participant in summary.Participants)
        {
            if (AddressIdentity.TryGetActorId(participant, out var participantId)
                && participantId == caller.Id)
            {
                return true;
            }
        }
        return false;
    }

    private static int ParseLimit(JsonElement args)
    {
        var limit = DefaultLimit;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("limit", out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out var raw))
        {
            limit = raw;
        }
        if (limit < 1) limit = 1;
        if (limit > MaxLimit) limit = MaxLimit;
        return limit;
    }

    private static int? TryReadIntArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var prop)
            || prop.ValueKind != JsonValueKind.Number
            || !prop.TryGetInt32(out var raw))
        {
            return null;
        }
        return raw;
    }

    private static string RequireStringArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var prop)
            || prop.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Missing required argument '{name}'.");
        }
        var raw = prop.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException($"Argument '{name}' must be a non-empty string.");
        }
        return raw;
    }

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement JsonNull()
    {
        using var doc = JsonDocument.Parse("null");
        return doc.RootElement.Clone();
    }

    private static JsonElement SerializeEngagements(
        IReadOnlyList<ThreadSummary> summaries, int limit)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("engagements");
            writer.WriteStartArray();
            foreach (var summary in summaries)
            {
                WriteEngagement(writer, summary);
            }
            writer.WriteEndArray();
            writer.WriteNumber("count", summaries.Count);
            writer.WriteNumber("limit", limit);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement SerializeHistory(
        ThreadSummary summary, IReadOnlyList<ThreadEvent> events)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("participants");
            writer.WriteStartArray();
            foreach (var participant in summary.Participants)
            {
                writer.WriteStringValue(participant);
            }
            writer.WriteEndArray();
            writer.WriteString("last_activity", summary.LastActivity);
            writer.WriteString("created_at", summary.CreatedAt);
            writer.WriteNumber("event_count", summary.EventCount);
            writer.WriteString("origin", summary.Origin);
            writer.WriteString("summary", summary.Summary);
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var ev in events)
            {
                WriteMessage(writer, ev);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement SerializeHits(
        IReadOnlyList<ThreadSearchHit> hits, int limit)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("hits");
            writer.WriteStartArray();
            foreach (var hit in hits)
            {
                writer.WriteStartObject();
                writer.WriteString("message_id", GuidFormatter.Format(hit.MessageId));
                writer.WriteString("timestamp", hit.Timestamp);
                writer.WriteString("from", hit.From);
                writer.WriteString("to", hit.To);
                writer.WriteString("body", hit.Body);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("count", hits.Count);
            writer.WriteNumber("limit", limit);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement SerializeMessages(MessageLookup lookup)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var hit in lookup.Messages)
            {
                writer.WriteStartObject();
                writer.WriteString("message_id", GuidFormatter.Format(hit.MessageId));
                writer.WriteString("timestamp", hit.Timestamp);
                writer.WriteString("from", hit.From);
                writer.WriteString("to", hit.To);
                writer.WriteString("body", hit.Body);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            // Collapsed skipped bucket (#2990): a flat list of the requested
            // ids we did not return — unknown, forbidden, or malformed, with
            // the reasons deliberately indistinguishable.
            writer.WritePropertyName("skipped");
            writer.WriteStartArray();
            foreach (var id in lookup.Skipped)
            {
                writer.WriteStringValue(id);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void WriteEngagement(Utf8JsonWriter writer, ThreadSummary summary)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("participants");
        writer.WriteStartArray();
        foreach (var participant in summary.Participants)
        {
            writer.WriteStringValue(participant);
        }
        writer.WriteEndArray();
        writer.WriteString("last_activity", summary.LastActivity);
        writer.WriteString("created_at", summary.CreatedAt);
        writer.WriteNumber("event_count", summary.EventCount);
        writer.WriteString("origin", summary.Origin);
        writer.WriteString("summary", summary.Summary);
        writer.WriteEndObject();
    }

    private static void WriteMessage(Utf8JsonWriter writer, ThreadEvent ev)
    {
        writer.WriteStartObject();
        if (ev.MessageId is { } mid)
        {
            writer.WriteString("message_id", GuidFormatter.Format(mid));
        }
        else
        {
            writer.WriteNull("message_id");
        }
        writer.WriteString("timestamp", ev.Timestamp);
        if (ev.From is { } from)
        {
            writer.WriteString("from", from);
        }
        else
        {
            writer.WriteNull("from");
        }
        if (ev.To is { } to)
        {
            writer.WriteString("to", to);
        }
        else
        {
            writer.WriteNull("to");
        }
        if (ev.Body is { } body)
        {
            writer.WriteString("body", body);
        }
        else
        {
            writer.WriteNull("body");
        }
        writer.WriteEndObject();
    }
}
