// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Memory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Platform-level <see cref="ISkillRegistry"/> implementing the
/// durable-store <c>sv.memory.*</c> tools (#2342). Lets the agent runtime
/// read, write, and recall its own memory at runtime — agent-wide recall
/// as well as per-conversation working notes — without any operator-tier
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
/// <b>Typed content variants, object-primary</b> (#3038, ADR-0065). The
/// content union is split into typed variants so encoding stays
/// consistent: <c>sv.memory.add</c> / <c>sv.memory.update</c> take
/// <i>structured</i> JSON (object / array / number / boolean) and are the
/// unqualified default; <c>sv.memory.text.add</c> / <c>sv.memory.text.update</c>
/// take a plain-text string. Both persist to the same <c>jsonb</c> column
/// (the text variant stores a JSON string), so this is a tool-contract
/// split only — no storage change, and content still round-trips with its
/// JSON type preserved.
/// </para>
/// <para>
/// <b>Conversation = participant set</b> (#3041 Part A, ADR-0065). A
/// memory verb identifies a conversation solely by an optional
/// <c>participants: [id, …]</c> — there is no <c>scope</c> enum and no
/// <c>thread_id</c> / <c>conversation_id</c> on the wire. Present →
/// memory for that conversation (the caller is auto-included); absent →
/// agent-wide memory recalled across all the caller's conversations.
/// Internally the participant set resolves to the same participant-set
/// key <see cref="SvMemoryHistoryRegistry"/> uses (<see cref="IThreadRegistry"/>),
/// and the entry is stored against that key's thread binding — so memory
/// and shared history share one conversation identifier. The internal
/// <c>thread_id</c> binding and the stored rows are unchanged.
/// </para>
/// <para>
/// <b>Recall.</b> <c>sv.memory.list</c> / <c>sv.memory.search</c> return a
/// single bucket per call: with no <c>participants</c> they recall the
/// caller's agent-wide entries; with <c>participants</c> they recall only
/// that conversation's entries. The two buckets are addressed
/// independently — an agent may record to one and read from the other in
/// the same turn.
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
    /// <summary>Tool name for <c>sv.memory.add</c> (structured content).</summary>
    public const string MemoryAddTool = "sv.memory.add";

    /// <summary>Tool name for <c>sv.memory.text.add</c> (plain-text content).</summary>
    public const string MemoryTextAddTool = "sv.memory.text.add";

    /// <summary>Tool name for <c>sv.memory.get</c>.</summary>
    public const string MemoryGetTool = "sv.memory.get";

    /// <summary>Tool name for <c>sv.memory.list</c>.</summary>
    public const string MemoryListTool = "sv.memory.list";

    /// <summary>Tool name for <c>sv.memory.search</c>.</summary>
    public const string MemorySearchTool = "sv.memory.search";

    /// <summary>Tool name for <c>sv.memory.update</c> (structured content).</summary>
    public const string MemoryUpdateTool = "sv.memory.update";

    /// <summary>Tool name for <c>sv.memory.text.update</c> (plain-text content).</summary>
    public const string MemoryTextUpdateTool = "sv.memory.text.update";

    /// <summary>Tool name for <c>sv.memory.delete</c>.</summary>
    public const string MemoryDeleteTool = "sv.memory.delete";

    /// <summary>Default page size for list tools.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Maximum allowed page size.</summary>
    public const int MaxLimit = 200;

    // The participant-set fragment shared by add / list / search. Mirrors
    // sv.memory.history_with so the conversation identifier is uniform
    // across the whole sv.memory.* surface (#3041 Part A).
    private const string AddParticipantsDescription =
        "Optional. Store this entry as memory for the conversation with these participants " +
        "(you are auto-included, so do not list yourself). Omit to store agent-wide memory, " +
        "recalled across all your conversations.";

    private const string RecallParticipantsDescription =
        "Optional. Recall memory for the conversation with these participants (you are " +
        "auto-included, so do not list yourself). Omit to recall your agent-wide memory — " +
        "the entries that apply across all your conversations.";

    private static readonly JsonElement MemoryAddSchema = ParseSchema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["content"],
          "properties": {
            "content": { "type": ["object", "array", "number", "boolean"], "description": "Structured memory content — a JSON object, array, number, or boolean. Stored as structured JSON and round-tripped with its type preserved, so field-level recall stays possible. For a plain-text note, call sv.memory.text.add instead." },
            "participants": {
              "type": "array",
              "minItems": 1,
              "description": "{{AddParticipantsDescription}}",
              "items": { "type": "string", "description": "Canonical scheme:no-dash-hex address." }
            },
            "source": { "type": "string", "description": "Optional origin reference (e.g. a message id or document reference)." }
          }
        }
        """);

    private static readonly JsonElement MemoryTextAddSchema = ParseSchema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["content"],
          "properties": {
            "content": { "type": "string", "description": "Plain-text memory content, stored and round-tripped as a string. For structured state (a JSON object/array), call sv.memory.add instead." },
            "participants": {
              "type": "array",
              "minItems": 1,
              "description": "{{AddParticipantsDescription}}",
              "items": { "type": "string", "description": "Canonical scheme:no-dash-hex address." }
            },
            "source": { "type": "string", "description": "Optional origin reference (e.g. a message id or document reference)." }
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

    private static readonly JsonElement MemoryListSchema = ParseSchema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "participants": {
              "type": "array",
              "minItems": 1,
              "description": "{{RecallParticipantsDescription}}",
              "items": { "type": "string", "description": "Canonical scheme:no-dash-hex address." }
            },
            "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50 },
            "offset": { "type": "integer", "minimum": 0, "default": 0 }
          }
        }
        """);

    private static readonly JsonElement MemorySearchSchema = ParseSchema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["query"],
          "properties": {
            "query": { "type": "string", "description": "Free-text search query." },
            "participants": {
              "type": "array",
              "minItems": 1,
              "description": "{{RecallParticipantsDescription}}",
              "items": { "type": "string", "description": "Canonical scheme:no-dash-hex address." }
            },
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
            "content": { "type": ["object", "array", "number", "boolean"], "description": "Replacement structured content (a JSON object, array, number, or boolean). Its JSON type may differ from the original. Omit to leave content untouched. For a plain-text note, call sv.memory.text.update instead." }
          }
        }
        """);

    private static readonly JsonElement MemoryTextUpdateSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["id"],
          "properties": {
            "id": { "type": "string", "description": "Stable Guid identifier of the memory entry to mutate." },
            "content": { "type": "string", "description": "Replacement plain-text content. Its JSON type may differ from the original. Omit to leave content untouched. For structured state (a JSON object/array), call sv.memory.update instead." }
          }
        }
        """);

    private readonly IMemoryStore _memoryStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    /// <summary>Builds the registry with its store + scope dependencies.</summary>
    public SvMemorySkillRegistry(
        IMemoryStore memoryStore,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        _memoryStore = memoryStore;
        _scopeFactory = scopeFactory;
        _logger = loggerFactory.CreateLogger<SvMemorySkillRegistry>();

        _tools = new[]
        {
            new ToolDefinition(
                MemoryAddTool,
                "Capture a new memory entry owned by the calling agent or unit, with " +
                "structured JSON content (an object, array, number, or boolean) — the " +
                "object-primary default; for a plain-text note use sv.memory.text.add. " +
                "Omit participants for agent-wide memory recalled across all your " +
                "conversations (the default); pass a conversation's participants to keep " +
                "the entry to that conversation only. Content round-trips with its type " +
                "preserved. Returns { id, owner, content, source, created_at, updated_at }.",
                MemoryAddSchema,
                ToolCategories.Memory),
            new ToolDefinition(
                MemoryTextAddTool,
                "Capture a new memory entry whose content is a plain-text string — the " +
                "text counterpart to sv.memory.add (use sv.memory.add for structured " +
                "JSON). Omit participants for agent-wide memory; pass a conversation's " +
                "participants to keep the note to that conversation only. Returns " +
                "{ id, owner, content, source, created_at, updated_at }.",
                MemoryTextAddSchema,
                ToolCategories.Memory),
            new ToolDefinition(
                MemoryGetTool,
                "Fetch a single memory entry by id; content comes back with its stored " +
                "JSON type (a string for a text note, an object/array for structured " +
                "state). Returns null when no entry with that id is owned by the calling " +
                "agent or unit.",
                MemoryGetSchema,
                ToolCategories.Memory),
            new ToolDefinition(
                MemoryListTool,
                "List memory entries owned by the caller, most-recent first. With no " +
                "participants you see your agent-wide entries; pass a conversation's " +
                "participants to see only that conversation's entries instead. " +
                "Pagination via limit (default 50, max 200) and offset.",
                MemoryListSchema,
                ToolCategories.Memory),
            new ToolDefinition(
                MemorySearchTool,
                "Free-text search over the caller's memory entries. Backed by " +
                "Postgres full-text search; results are ordered by relevance " +
                "(highest first). With no participants the search spans your agent-wide " +
                "entries; pass a conversation's participants to search only that " +
                "conversation's entries instead. limit defaults to 50 (max 200).",
                MemorySearchSchema,
                ToolCategories.Memory),
            new ToolDefinition(
                MemoryUpdateTool,
                "Mutate an existing memory entry with structured JSON content (an object, " +
                "array, number, or boolean) — the text counterpart is sv.memory.text.update. " +
                "Pass `content` to replace the entry's content; omit it to leave the entry " +
                "untouched. Returns the updated entry on success, or { updated: false, " +
                "reason: \"not_found\" } when no entry with that id is owned by you — the id " +
                "may be stale, so re-read with sv.memory.list or sv.memory.search to get a " +
                "current id.",
                MemoryUpdateSchema,
                ToolCategories.Memory),
            new ToolDefinition(
                MemoryTextUpdateTool,
                "Mutate an existing memory entry with a plain-text string — the text " +
                "counterpart to sv.memory.update (use sv.memory.update for structured " +
                "JSON). Pass `content` to replace the entry's content; omit it to leave " +
                "the entry untouched. Returns the updated entry on success, or " +
                "{ updated: false, reason: \"not_found\" } when no entry with that id is " +
                "owned by you.",
                MemoryTextUpdateSchema,
                ToolCategories.Memory),
            new ToolDefinition(
                MemoryDeleteTool,
                "Delete a memory entry by id. Returns { deleted: true } on success " +
                "and { deleted: false } when no entry with that id is owned by the " +
                "caller.",
                MemoryGetSchema,
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
            MemoryAddTool => MemoryAddAsync(arguments, context, structured: true, cancellationToken),
            MemoryTextAddTool => MemoryAddAsync(arguments, context, structured: false, cancellationToken),
            MemoryGetTool => MemoryGetAsync(arguments, context, cancellationToken),
            MemoryListTool => MemoryListAsync(arguments, context, cancellationToken),
            MemorySearchTool => MemorySearchAsync(arguments, context, cancellationToken),
            MemoryUpdateTool => MemoryUpdateAsync(arguments, context, structured: true, cancellationToken),
            MemoryTextUpdateTool => MemoryUpdateAsync(arguments, context, structured: false, cancellationToken),
            MemoryDeleteTool => MemoryDeleteAsync(arguments, context, cancellationToken),
            _ => throw new SkillNotFoundException(toolName),
        };
    }

    private async Task<JsonElement> MemoryAddAsync(JsonElement args, ToolCallContext context, bool structured, CancellationToken ct)
    {
        var owner = ParseCaller(context);
        var content = structured
            ? RequireStructuredContent(args, "content")
            : RequireTextContent(args, "content");
        var source = TryReadStringArg(args, "source");
        var threadId = await ResolveConversationBindingAsync(args, context, ct);

        var entry = await _memoryStore.AddAsync(owner, content, source, threadId, ct);
        _logger.LogDebug(
            "sv.memory.add owner={Owner} conversation={Conversation} id={Id}",
            owner, threadId is null ? "agent-wide" : "conversation", entry.Id);
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
        var (scope, threadId) = await ResolveRecallFilterAsync(args, context, ct);
        var (limit, offset) = ParsePaging(args);
        var entries = await _memoryStore.ListAsync(owner, scope, threadId, limit, offset, ct);
        return SerializePagedMemoryList(entries, limit, offset);
    }

    private async Task<JsonElement> MemorySearchAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var owner = ParseCaller(context);
        var query = RequireStringArg(args, "query");
        var (scope, threadId) = await ResolveRecallFilterAsync(args, context, ct);
        var (limit, _) = ParsePaging(args);
        var entries = await _memoryStore.SearchAsync(owner, query, scope, threadId, limit, ct);
        return SerializeRawMemoryList(entries);
    }

    private async Task<JsonElement> MemoryUpdateAsync(JsonElement args, ToolCallContext context, bool structured, CancellationToken ct)
    {
        var owner = ParseCaller(context);
        var id = RequireGuidArg(args, "id");
        var content = structured
            ? TryReadStructuredContent(args, "content")
            : TryReadTextContent(args, "content");
        var entry = await _memoryStore.UpdateAsync(owner, id, content, ct);
        // A stale / unknown id is a routine, self-correctable condition — not
        // a platform fault (#3036). Return a clean not-found result the model
        // can act on; a thrown SpringException would surface as an
        // Error-severity "tool failed" the model reads as a crash instead of
        // "re-read your memory for a fresh id".
        return entry is null ? SerializeUpdateNotFound(id) : SerializeEntry(entry);
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

    /// <summary>
    /// Resolves the optional <c>participants</c> argument to the internal
    /// thread binding for a memory entry (#3041 Part A). Absent / empty →
    /// <c>null</c> (agent-wide memory). Present → the participant set
    /// (caller auto-included) resolved through <see cref="IThreadRegistry"/>
    /// to the same participant-set key <c>sv.memory.history_with</c> uses,
    /// so the entry is bound to that conversation.
    /// </summary>
    private async Task<Guid?> ResolveConversationBindingAsync(
        JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var others = TryReadParticipantArray(args, "participants");
        if (others is not { Count: > 0 })
        {
            return null;
        }

        var caller = ParseCaller(context);
        var participants = BuildParticipantSet(caller, others);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<IThreadRegistry>();
        var threadId = await registry.GetOrCreateAsync(participants, ct);

        // GetOrCreateAsync returns the canonical no-dash hex form; parse it
        // back to the Guid the store binds entries to.
        if (!GuidFormatter.TryParse(threadId, out var guid))
        {
            throw new SpringException(
                $"Thread registry returned an unparseable conversation id '{threadId}'.");
        }
        return guid;
    }

    /// <summary>
    /// Builds the (scope filter, thread binding) pair for the recall path
    /// (#3041 Part A). No participants → agent-wide memory only
    /// (<see cref="MemoryScope.Agent"/>, no thread). Participants → that
    /// conversation's memory only: <see cref="MemoryScope.Thread"/> excludes
    /// agent-wide rows and the thread filter pins the bucket to the resolved
    /// conversation, so the store's combined predicate narrows to exactly
    /// that conversation's entries.
    /// </summary>
    private async Task<(MemoryScope? Scope, Guid? ThreadId)> ResolveRecallFilterAsync(
        JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var threadId = await ResolveConversationBindingAsync(args, context, ct);
        return threadId is null
            ? (MemoryScope.Agent, null)
            : (MemoryScope.Thread, threadId);
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

    /// <summary>
    /// Reads the required structured <c>content</c> for the object-primary
    /// <c>add</c> / <c>update</c> variants: a JSON object, array, number, or
    /// boolean. A JSON string is rejected with a pointer to the
    /// <c>sv.memory.text.*</c> variant so the typed split stays consistent
    /// (#3038). The value is returned detached (<see cref="JsonElement.Clone"/>)
    /// so it outlives the request's argument document.
    /// </summary>
    private static JsonElement RequireStructuredContent(JsonElement args, string name)
    {
        var prop = RequirePresentContent(args, name);
        RejectStringContent(prop, name);
        return prop.Clone();
    }

    /// <summary>
    /// Optional-content counterpart of <see cref="RequireStructuredContent"/>
    /// (partial-update semantics): <c>null</c> when omitted or JSON
    /// <c>null</c>; otherwise the structured value, rejecting a JSON string.
    /// </summary>
    private static JsonElement? TryReadStructuredContent(JsonElement args, string name)
    {
        if (!TryGetPresentContent(args, name, out var prop))
        {
            return null;
        }
        RejectStringContent(prop, name);
        return prop.Clone();
    }

    /// <summary>
    /// Reads the required plain-text <c>content</c> for the
    /// <c>sv.memory.text.*</c> variants: a non-empty / non-whitespace JSON
    /// string. A non-string value is rejected with a pointer to the
    /// structured <c>add</c> / <c>update</c> variant (#3038).
    /// </summary>
    private static JsonElement RequireTextContent(JsonElement args, string name)
    {
        var prop = RequirePresentContent(args, name);
        EnsureTextContent(prop, name);
        return prop.Clone();
    }

    /// <summary>
    /// Optional-content counterpart of <see cref="RequireTextContent"/>:
    /// <c>null</c> when omitted or JSON <c>null</c>; otherwise the string
    /// value, rejecting a non-string or empty value.
    /// </summary>
    private static JsonElement? TryReadTextContent(JsonElement args, string name)
    {
        if (!TryGetPresentContent(args, name, out var prop))
        {
            return null;
        }
        EnsureTextContent(prop, name);
        return prop.Clone();
    }

    /// <summary>
    /// Resolves a required <c>content</c> property, throwing a retry-guiding
    /// error when it is missing, JSON <c>null</c>, or undefined.
    /// </summary>
    private static JsonElement RequirePresentContent(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var prop) ||
            prop.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException($"Missing required argument '{name}'.");
        }
        return prop;
    }

    /// <summary>
    /// Resolves an optional <c>content</c> property, returning <c>false</c>
    /// when it is omitted or JSON <c>null</c> (leave-untouched semantics).
    /// </summary>
    private static bool TryGetPresentContent(JsonElement args, string name, out JsonElement prop)
    {
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty(name, out prop) &&
            prop.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return true;
        }
        prop = default;
        return false;
    }

    private static void RejectStringContent(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            throw new ArgumentException(
                $"Argument '{name}' on sv.memory.add / sv.memory.update must be structured JSON " +
                "(an object, array, number, or boolean). For a plain-text note, call " +
                "sv.memory.text.add / sv.memory.text.update instead.");
        }
    }

    private static void EnsureTextContent(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                $"Argument '{name}' on sv.memory.text.add / sv.memory.text.update must be a plain-text " +
                "string. For structured JSON (an object or array), call sv.memory.add / sv.memory.update instead.");
        }
        if (string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"Argument '{name}' must be a non-empty string.");
        }
    }

    private static Guid RequireGuidArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                $"sv.memory.* requires a '{name}': the no-dash 32-char hex id of a memory entry " +
                "(the `id` returned by sv.memory.add, or one from sv.memory.list / sv.memory.search).");
        }
        if (!GuidFormatter.TryParse(prop.GetString(), out var guid))
        {
            throw new ArgumentException(
                $"Argument '{name}' = '{prop.GetString()}' is not a valid memory id. Pass the no-dash " +
                "32-char hex `id` of a memory entry (from sv.memory.add / sv.memory.list / sv.memory.search).");
        }
        return guid;
    }

    /// <summary>
    /// Reads the optional <c>participants</c> array as canonical addresses,
    /// or <c>null</c> when omitted / not an array. Each element must be a
    /// valid Spring Voyage address. Mirrors <see cref="SvMemoryHistoryRegistry"/>
    /// so the conversation identifier is uniform across the memory surface.
    /// </summary>
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

    /// <summary>
    /// Canonicalises <c>{caller} ∪ supplied</c> so the caller never lists
    /// itself in <c>participants</c> — the same auto-include
    /// <see cref="SvMemoryHistoryRegistry"/> applies, so a memory verb and
    /// <c>sv.memory.history_with</c> resolve the same participant-set key.
    /// </summary>
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

    /// <summary>
    /// Clean not-found result for the <c>update</c> variants (#3036). Distinct
    /// from the success shape (a full entry) so the model can branch on it,
    /// and returned with <c>isError=false</c> so a stale id reads as a
    /// recoverable condition rather than a platform crash.
    /// </summary>
    private static JsonElement SerializeUpdateNotFound(Guid id)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("updated", false);
            writer.WriteString("reason", "not_found");
            writer.WriteString("id", GuidFormatter.Format(id));
            writer.WriteEndObject();
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

    /// <summary>
    /// Writes the agent-facing entry shape. The internal conversation
    /// binding is never surfaced (#3041 Part A): the caller already knows
    /// the bucket from how it queried (agent-wide vs a named conversation),
    /// so the entry carries no <c>scope</c> / <c>thread_id</c>.
    /// </summary>
    private static void WriteEntry(Utf8JsonWriter writer, MemoryEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("id", GuidFormatter.Format(entry.Id));
        writer.WritePropertyName("owner");
        writer.WriteStartObject();
        writer.WriteString("scheme", entry.Owner.Scheme);
        writer.WriteString("id", entry.Owner.Path);
        writer.WriteEndObject();
        writer.WritePropertyName("content");
        entry.Content.WriteTo(writer);
        if (entry.Source is { } src)
        {
            writer.WriteString("source", src);
        }
        else
        {
            writer.WriteNull("source");
        }
        writer.WriteString("created_at", entry.CreatedAt);
        writer.WriteString("updated_at", entry.UpdatedAt);
        writer.WriteEndObject();
    }
}
