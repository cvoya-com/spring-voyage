// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Memory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// Platform-level <see cref="ISkillRegistry"/> implementing the
/// <c>sv.memory_*</c> tools (#2342). Lets the agent runtime read,
/// write, and recall its own memory at runtime — long-term recall as
/// well as thread-scoped working notes — without any operator-tier
/// surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Caller scope.</b> Every tool keys off the caller identity threaded
/// in via <see cref="ToolCallContext"/>: <c>CallerId</c> + <c>CallerKind</c>
/// build the owning <see cref="Address"/>, and that address is passed
/// to <see cref="IMemoryStore"/> on every call. An agent cannot read
/// another agent's memory and a unit cannot read an agent's memory
/// through this surface — owner-scoping is non-negotiable.
/// </para>
/// <para>
/// <b>Short-term entries.</b> <c>sv.memory_add</c> with
/// <c>kind = "short_term"</c> defaults <c>thread_id</c> to the caller's
/// active thread (sourced from <see cref="ToolCallContext.ThreadId"/>)
/// when the tool argument is omitted. Explicit <c>thread_id</c>
/// arguments override the default, which the LLM uses when stitching
/// notes onto a parallel thread it already remembers.
/// </para>
/// <para>
/// <b>UUID-only public surface.</b> The wire shape never accepts or
/// returns platform-internal addresses. Memory ids cross the boundary
/// as no-dash 32-char hex Guids. Owner addresses are derived from the
/// caller — they are never carried on the wire.
/// </para>
/// </remarks>
public sealed class SvMemorySkillRegistry : ISkillRegistry
{
    /// <summary>Tool name for <c>sv.memory_add</c>.</summary>
    public const string MemoryAddTool = "sv.memory_add";

    /// <summary>Tool name for <c>sv.memory_get</c>.</summary>
    public const string MemoryGetTool = "sv.memory_get";

    /// <summary>Tool name for <c>sv.memory_list</c>.</summary>
    public const string MemoryListTool = "sv.memory_list";

    /// <summary>Tool name for <c>sv.memory_search</c>.</summary>
    public const string MemorySearchTool = "sv.memory_search";

    /// <summary>Tool name for <c>sv.memory_update</c>.</summary>
    public const string MemoryUpdateTool = "sv.memory_update";

    /// <summary>Tool name for <c>sv.memory_delete</c>.</summary>
    public const string MemoryDeleteTool = "sv.memory_delete";

    /// <summary>Default page size for list tools.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Maximum allowed page size.</summary>
    public const int MaxLimit = 200;

    private const string KindLongTerm = "long_term";
    private const string KindShortTerm = "short_term";

    private static readonly JsonElement MemoryAddSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["content"],
          "properties": {
            "content": { "type": "string", "description": "Raw entry text." },
            "kind": { "type": "string", "enum": ["long_term", "short_term"], "default": "long_term" },
            "source": { "type": "string", "description": "Optional origin reference (message id, conversation id, document reference)." },
            "thread_id": { "type": "string", "description": "Stable Guid identifier of the thread to associate the entry with. Required for short_term entries; defaults to the active thread when omitted." }
          }
        }
        """);

    private static readonly JsonElement MemoryGetSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["id"],
          "properties": {
            "id": { "type": "string", "description": "Stable Guid identifier of the memory entry." }
          }
        }
        """);

    private static readonly JsonElement MemoryListSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "kind": { "type": "string", "enum": ["long_term", "short_term"], "description": "Optional kind filter." },
            "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50 },
            "offset": { "type": "integer", "minimum": 0, "default": 0 }
          }
        }
        """);

    private static readonly JsonElement MemorySearchSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["query"],
          "properties": {
            "query": { "type": "string", "description": "Free-text search query." },
            "kind": { "type": "string", "enum": ["long_term", "short_term"], "description": "Optional kind filter." },
            "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50 }
          }
        }
        """);

    private static readonly JsonElement MemoryUpdateSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["id"],
          "properties": {
            "id": { "type": "string", "description": "Stable Guid identifier of the memory entry to mutate." },
            "content": { "type": "string", "description": "Replacement content. Omit to leave content untouched." }
          }
        }
        """);

    private readonly IMemoryStore _memoryStore;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    /// <summary>Builds the registry with its store dependency.</summary>
    public SvMemorySkillRegistry(
        IMemoryStore memoryStore,
        ILoggerFactory loggerFactory)
    {
        _memoryStore = memoryStore;
        _logger = loggerFactory.CreateLogger<SvMemorySkillRegistry>();

        _tools = new[]
        {
            new ToolDefinition(
                MemoryAddTool,
                "Capture a new memory entry owned by the calling agent or unit. " +
                "Long-term entries survive across conversations; short-term entries " +
                "are scoped to a thread and intended for working notes — pass " +
                "kind='short_term' (defaults to long_term). Returns " +
                "{ id, owner, kind, content, source, thread_id, created_at, updated_at }.",
                MemoryAddSchema),
            new ToolDefinition(
                MemoryGetTool,
                "Fetch a single memory entry by id. Returns null when no entry " +
                "with that id is owned by the calling agent or unit.",
                MemoryGetSchema),
            new ToolDefinition(
                MemoryListTool,
                "List memory entries owned by the caller, most-recent first. " +
                "Optional filter: kind ('long_term' / 'short_term'). " +
                "Pagination via limit (default 50, max 200) and offset.",
                MemoryListSchema),
            new ToolDefinition(
                MemorySearchTool,
                "Free-text search over the caller's memory entries. Backed by " +
                "Postgres full-text search; results are ordered by relevance " +
                "(highest first). Optional kind filter narrows the search " +
                "scope. limit defaults to 50 (max 200).",
                MemorySearchSchema),
            new ToolDefinition(
                MemoryUpdateTool,
                "Mutate an existing memory entry. Pass `content` to replace the " +
                "entry's text; omit it to leave the entry untouched.",
                MemoryUpdateSchema),
            new ToolDefinition(
                MemoryDeleteTool,
                "Delete a memory entry by id. Returns { deleted: true } on success " +
                "and { deleted: false } when no entry with that id is owned by the " +
                "caller.",
                MemoryGetSchema),
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
            MemoryAddTool => MemoryAddAsync(arguments, context, cancellationToken),
            MemoryGetTool => MemoryGetAsync(arguments, context, cancellationToken),
            MemoryListTool => MemoryListAsync(arguments, context, cancellationToken),
            MemorySearchTool => MemorySearchAsync(arguments, context, cancellationToken),
            MemoryUpdateTool => MemoryUpdateAsync(arguments, context, cancellationToken),
            MemoryDeleteTool => MemoryDeleteAsync(arguments, context, cancellationToken),
            _ => throw new SkillNotFoundException(toolName),
        };
    }

    private async Task<JsonElement> MemoryAddAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var owner = ParseCaller(context);
        var content = RequireStringArg(args, "content");
        var kind = ParseKindArg(args, MemoryKind.LongTerm);
        var source = TryReadStringArg(args, "source");
        var threadId = ParseThreadId(args, kind, context);

        var entry = await _memoryStore.AddAsync(owner, kind, content, source, threadId, ct);
        _logger.LogDebug("sv.memory_add owner={Owner} kind={Kind} id={Id}", owner, kind, entry.Id);
        return SerializeEntry(entry);
    }

    private async Task<JsonElement> MemoryGetAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var owner = ParseCaller(context);
        var id = RequireGuidArg(args, "id");
        var entry = await _memoryStore.GetAsync(owner, id, ct);
        return entry is null ? JsonNull() : SerializeEntry(entry);
    }

    private async Task<JsonElement> MemoryListAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var owner = ParseCaller(context);
        var kind = TryParseKind(args);
        var (limit, offset) = ParsePaging(args);
        var entries = await _memoryStore.ListAsync(owner, kind, limit, offset, ct);
        return SerializePagedMemoryList(entries, limit, offset);
    }

    private async Task<JsonElement> MemorySearchAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var owner = ParseCaller(context);
        var query = RequireStringArg(args, "query");
        var kind = TryParseKind(args);
        var (limit, _) = ParsePaging(args);
        var entries = await _memoryStore.SearchAsync(owner, query, kind, limit, ct);
        return SerializeRawMemoryList(entries);
    }

    private async Task<JsonElement> MemoryUpdateAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var owner = ParseCaller(context);
        var id = RequireGuidArg(args, "id");
        var content = TryReadStringArg(args, "content");
        var entry = await _memoryStore.UpdateAsync(owner, id, content, ct);
        if (entry is null)
        {
            throw new SpringException($"Memory entry '{id:N}' not found for caller {owner}.");
        }
        return SerializeEntry(entry);
    }

    private async Task<JsonElement> MemoryDeleteAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var owner = ParseCaller(context);
        var id = RequireGuidArg(args, "id");
        var deleted = await _memoryStore.DeleteAsync(owner, id, ct);
        return SerializeDeleted(deleted);
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
        var scheme = string.IsNullOrWhiteSpace(context.CallerKind) ? Address.AgentScheme : context.CallerKind;
        return new Address(scheme, guid);
    }

    private static MemoryKind ParseKindArg(JsonElement args, MemoryKind fallback)
    {
        var explicitKind = TryParseKind(args);
        return explicitKind ?? fallback;
    }

    private static MemoryKind? TryParseKind(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!args.TryGetProperty("kind", out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var raw = prop.GetString();
        if (string.Equals(raw, KindLongTerm, StringComparison.Ordinal))
        {
            return MemoryKind.LongTerm;
        }
        if (string.Equals(raw, KindShortTerm, StringComparison.Ordinal))
        {
            return MemoryKind.ShortTerm;
        }
        throw new ArgumentException($"Argument 'kind' must be '{KindLongTerm}' or '{KindShortTerm}'.");
    }

    private static Guid? ParseThreadId(JsonElement args, MemoryKind kind, ToolCallContext context)
    {
        var explicitId = TryReadGuidArg(args, "thread_id");
        if (explicitId.HasValue)
        {
            return explicitId;
        }
        if (kind == MemoryKind.LongTerm)
        {
            return null;
        }
        // Short-term default: inherit the caller's active thread when
        // ToolCallContext.ThreadId is a parseable Guid. The MCP server
        // passes the ThreadId as a 32-char no-dash hex string, but a
        // tool caller that builds a context manually may supply
        // something else — surface a deterministic error in that case.
        if (!GuidFormatter.TryParse(context.ThreadId, out var threadGuid))
        {
            throw new SpringException(
                "Short-term memory requires a thread id; the caller's active " +
                $"thread '{context.ThreadId}' is not a parseable Guid and no " +
                "explicit thread_id argument was supplied.");
        }
        return threadGuid;
    }

    private static (int Limit, int Offset) ParsePaging(JsonElement args)
    {
        var limit = DefaultLimit;
        var offset = 0;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var lv))
            {
                limit = lv;
            }
            if (args.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var ov))
            {
                offset = ov;
            }
        }
        if (limit < 1) limit = 1;
        if (limit > MaxLimit) limit = MaxLimit;
        if (offset < 0) offset = 0;
        return (limit, offset);
    }

    private static string RequireStringArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
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
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var raw = prop.GetString();
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    private static Guid RequireGuidArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Missing required argument '{name}'.");
        }
        if (!GuidFormatter.TryParse(prop.GetString(), out var guid))
        {
            throw new ArgumentException($"Argument '{name}' must be a Guid.");
        }
        return guid;
    }

    private static Guid? TryReadGuidArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        if (!GuidFormatter.TryParse(prop.GetString(), out var guid))
        {
            throw new ArgumentException($"Argument '{name}' must be a Guid.");
        }
        return guid;
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

    private static JsonElement SerializeEntry(MemoryEntry entry)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteEntry(writer, entry);
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement SerializePagedMemoryList(IReadOnlyList<MemoryEntry> entries, int limit, int offset)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("memories");
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                WriteEntry(writer, entry);
            }
            writer.WriteEndArray();
            writer.WriteNumber("count", entries.Count);
            writer.WriteNumber("limit", limit);
            writer.WriteNumber("offset", offset);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement SerializeRawMemoryList(IReadOnlyList<MemoryEntry> entries)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                WriteEntry(writer, entry);
            }
            writer.WriteEndArray();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement SerializeDeleted(bool deleted)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("deleted", deleted);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void WriteEntry(Utf8JsonWriter writer, MemoryEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("id", GuidFormatter.Format(entry.Id));
        writer.WritePropertyName("owner");
        writer.WriteStartObject();
        writer.WriteString("scheme", entry.Owner.Scheme);
        writer.WriteString("id", entry.Owner.Path);
        writer.WriteEndObject();
        writer.WriteString("kind", entry.Kind == MemoryKind.ShortTerm ? KindShortTerm : KindLongTerm);
        writer.WriteString("content", entry.Content);
        if (entry.Source is { } src)
        {
            writer.WriteString("source", src);
        }
        else
        {
            writer.WriteNull("source");
        }
        if (entry.ThreadId is { } tid)
        {
            writer.WriteString("thread_id", GuidFormatter.Format(tid));
        }
        else
        {
            writer.WriteNull("thread_id");
        }
        writer.WriteString("created_at", entry.CreatedAt);
        writer.WriteString("updated_at", entry.UpdatedAt);
        writer.WriteEndObject();
    }
}
