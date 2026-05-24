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
/// <c>sv.thread.*</c> tools (#2683). Lets the agent runtime inspect the
/// threads it participates in — list, fetch the timeline, search across
/// message bodies, and enumerate participants — without baking that
/// state into the system prompt or shipping the entire thread history
/// on every turn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Caller scope.</b> Every tool keys off the caller identity threaded
/// in via <see cref="ToolCallContext"/>: <c>CallerId</c> + <c>CallerKind</c>
/// build the caller's <see cref="Address"/>, and that address is passed
/// to <see cref="IThreadQueryService"/> / <see cref="IThreadRegistry"/>
/// as the participant filter. A caller never sees a thread it is not a
/// participant of — the same scoping that hides another agent's memory
/// hides another agent's threads through this surface.
/// </para>
/// <para>
/// <b>Why not surface the full A2A envelope on every turn?</b> The
/// runtime literally sees the <c>payload</c> text on a turn and inherits
/// the active <c>thread_id</c> through its session binding (ADR-0041).
/// The platform contract documents the rest of the envelope (sender,
/// message id, prior messages) but the runtime only pays for it when it
/// asks via these tools. Pulling the timeline on demand keeps the
/// per-turn prompt small while leaving the durable record reachable.
/// </para>
/// <para>
/// <b>UUID-only public surface.</b> Thread ids cross the wire as the
/// no-dash 32-char hex form (matching <see cref="GuidFormatter.Format"/>);
/// addresses use the canonical <c>scheme:&lt;32-hex&gt;</c> rendering
/// (matching <see cref="Address.ToString"/>).
/// </para>
/// </remarks>
public sealed class SvThreadSkillRegistry : ISkillRegistry
{
    /// <summary>Tool name for <c>sv.thread.list</c>.</summary>
    public const string ThreadListTool = "sv.thread.list";

    /// <summary>Tool name for <c>sv.thread.get</c>.</summary>
    public const string ThreadGetTool = "sv.thread.get";

    /// <summary>Tool name for <c>sv.thread.search</c>.</summary>
    public const string ThreadSearchTool = "sv.thread.search";

    /// <summary>Tool name for <c>sv.thread.participants</c>.</summary>
    public const string ThreadParticipantsTool = "sv.thread.participants";

    /// <summary>Default page size for list / search tools.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Maximum allowed page size.</summary>
    public const int MaxLimit = 200;

    private static readonly JsonElement ThreadListSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50, "description": "Maximum number of threads to return (default 50, max 200)." }
          }
        }
        """);

    private static readonly JsonElement ThreadGetSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["thread_id"],
          "properties": {
            "thread_id": { "type": "string", "description": "Stable Guid identifier of the thread (no-dash 32-char hex form; standard dashed UUID form is also accepted)." },
            "tail": { "type": "integer", "minimum": 1, "maximum": 500, "description": "When supplied, return only the most-recent N messages on the thread (default: every persisted message)." }
          }
        }
        """);

    private static readonly JsonElement ThreadSearchSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["query"],
          "properties": {
            "query": { "type": "string", "description": "Free-text query matched against persisted message bodies." },
            "thread_id": { "type": "string", "description": "Optional: scope the search to a single thread the caller participates in." },
            "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50, "description": "Maximum number of matching messages to return (default 50, max 200)." }
          }
        }
        """);

    private static readonly JsonElement ThreadParticipantsSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["thread_id"],
          "properties": {
            "thread_id": { "type": "string", "description": "Stable Guid identifier of the thread." }
          }
        }
        """);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    /// <summary>Builds the registry with its scoped read dependencies.</summary>
    /// <param name="scopeFactory">Scope factory for the per-call <see cref="IThreadQueryService"/> / <see cref="IThreadRegistry"/> reads.</param>
    /// <param name="loggerFactory">Logger factory for diagnostic logging.</param>
    public SvThreadSkillRegistry(
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = loggerFactory.CreateLogger<SvThreadSkillRegistry>();

        _tools = new[]
        {
            new ToolDefinition(
                ThreadListTool,
                "List the threads the calling agent or unit participates in, most-recent " +
                "activity first. Each entry carries { id, participants, last_activity, " +
                "created_at, event_count, origin, summary }. Pagination via limit (default " +
                "50, max 200). A thread is the set of participants on a conversation plus " +
                "the durable timeline of every message between them.",
                ThreadListSchema,
                ToolCategories.Thread),
            new ToolDefinition(
                ThreadGetTool,
                "Fetch the full message timeline for a single thread the caller " +
                "participates in. Returns { summary: { id, participants, ... }, messages: " +
                "[{ message_id, timestamp, from, to, body }, ...] } ordered oldest first. " +
                "Pass tail=N to return only the most-recent N messages — useful when an " +
                "agent only needs recent context. Returns null when no thread with the " +
                "supplied id is visible to the caller.",
                ThreadGetSchema,
                ToolCategories.Thread),
            new ToolDefinition(
                ThreadSearchTool,
                "Free-text search across persisted messages on the threads the caller " +
                "participates in. Returns { hits: [{ thread_id, message_id, timestamp, " +
                "from, to, body }, ...] } ordered by relevance (when the database supports " +
                "full-text search) or by recency otherwise. Optional thread_id narrows the " +
                "search to a single thread the caller participates in; limit defaults to " +
                "50 (max 200).",
                ThreadSearchSchema,
                ToolCategories.Thread),
            new ToolDefinition(
                ThreadParticipantsTool,
                "List the participant addresses for a single thread the caller " +
                "participates in. Returns { thread_id, participants: [\"agent:...\", " +
                "\"human:...\", ...] }. Resolve any participant to its display name / " +
                "role / expertise with sv.directory.lookup. Returns null when no thread " +
                "with the supplied id is visible to the caller.",
                ThreadParticipantsSchema,
                ToolCategories.Thread),
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
            ThreadListTool => ThreadListAsync(arguments, context, cancellationToken),
            ThreadGetTool => ThreadGetAsync(arguments, context, cancellationToken),
            ThreadSearchTool => ThreadSearchAsync(arguments, context, cancellationToken),
            ThreadParticipantsTool => ThreadParticipantsAsync(arguments, context, cancellationToken),
            _ => throw new SkillNotFoundException(toolName),
        };
    }

    private async Task<JsonElement> ThreadListAsync(
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
            "sv.thread.list caller={Caller} returned={Count}",
            caller, summaries.Count);

        return SerializeSummaries(summaries, limit);
    }

    private async Task<JsonElement> ThreadGetAsync(
        JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var caller = ParseCaller(context);
        var threadId = RequireStringArg(args, "thread_id");
        var tail = TryReadIntArg(args, "tail");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var queryService = scope.ServiceProvider.GetRequiredService<IThreadQueryService>();

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

        return SerializeDetail(detail.Summary, events);
    }

    private async Task<JsonElement> ThreadSearchAsync(
        JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var caller = ParseCaller(context);
        var query = RequireStringArg(args, "query");
        var threadId = TryReadStringArg(args, "thread_id");
        var limit = ParseLimit(args);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var queryService = scope.ServiceProvider.GetRequiredService<IThreadQueryService>();

        var hits = await queryService.SearchAsync(caller.ToString(), query, threadId, limit, ct);
        return SerializeHits(hits, limit);
    }

    private async Task<JsonElement> ThreadParticipantsAsync(
        JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var caller = ParseCaller(context);
        var threadId = RequireStringArg(args, "thread_id");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<IThreadRegistry>();

        var entry = await registry.ResolveAsync(threadId, ct);
        if (entry is null || !entry.Participants.Any(p => p.Id == caller.Id))
        {
            return JsonNull();
        }

        return SerializeParticipants(entry);
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

    private static string? TryReadStringArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var prop)
            || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var raw = prop.GetString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
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

    private static JsonElement SerializeSummaries(
        IReadOnlyList<ThreadSummary> summaries, int limit)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("threads");
            writer.WriteStartArray();
            foreach (var summary in summaries)
            {
                WriteSummary(writer, summary);
            }
            writer.WriteEndArray();
            writer.WriteNumber("count", summaries.Count);
            writer.WriteNumber("limit", limit);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement SerializeDetail(
        ThreadSummary summary, IReadOnlyList<ThreadEvent> events)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("summary");
            WriteSummary(writer, summary);
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
                writer.WriteString("thread_id", hit.ThreadId);
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

    private static JsonElement SerializeParticipants(ThreadRegistryEntry entry)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("thread_id", entry.ThreadId);
            writer.WritePropertyName("participants");
            writer.WriteStartArray();
            foreach (var address in entry.Participants)
            {
                writer.WriteStringValue(address.ToString());
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void WriteSummary(Utf8JsonWriter writer, ThreadSummary summary)
    {
        writer.WriteStartObject();
        writer.WriteString("id", summary.Id);
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
