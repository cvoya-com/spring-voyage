// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Platform-level <see cref="ISkillRegistry"/> implementing ADR-0056 §6's
/// tool-discovery surface: <c>sv.tools.list_categories</c> and
/// <c>sv.tools.list(category)</c>. The discovery surface lets a runtime
/// enumerate the capability categories it has access to without paying
/// for every per-tool schema on every turn — the prompt carries the
/// fundamental core plus the category index, and the runtime pulls a
/// category's tools on demand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Effective-grant scoping.</b> Both tools intersect the platform's
/// registered tool surface with the caller's effective grants
/// (<see cref="IToolGrantResolver"/>): a runtime never sees a category
/// it cannot use, and a category's listing never includes a tool the
/// runtime cannot invoke. The same gate <c>tools/list</c> applies on
/// the MCP server itself (#2379) — the per-category projection here
/// mirrors that contract one layer up.
/// </para>
/// <para>
/// <b>"No tool known by name alone" invariant (ADR-0056 §6).</b>
/// <c>sv.tools.list</c> returns the full
/// <see cref="ToolDefinition.InputSchema"/> alongside name and
/// description so a runtime that has heard of a tool already has
/// everything it needs to call it. A separate <c>sv.tools.describe</c>
/// is intentionally absent.
/// </para>
/// <para>
/// <b>Category descriptions and usage guidance.</b> The per-category
/// summaries and the <c>usage_guidance</c> string the listing tool
/// returns live as static data on this registry — the categories are a
/// platform contract, not a per-registry surface. Registries that ship
/// tools in a category populate the category through the
/// <see cref="ToolDefinition.Category"/> field; the discovery surface
/// joins the two sides at enumeration time.
/// </para>
/// </remarks>
public sealed class SvToolsDiscoverySkillRegistry : ISkillRegistry
{
    /// <summary>Tool name for <c>sv.tools.list_categories</c>.</summary>
    public const string ListCategoriesTool = "sv.tools.list_categories";

    /// <summary>Tool name for <c>sv.tools.list</c>.</summary>
    public const string ListTool = "sv.tools.list";

    private static readonly JsonElement EmptyObjectSchema = ParseSchema("""
        { "type": "object", "additionalProperties": false, "properties": {} }
        """);
    private static readonly JsonElement ListArgSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["category"],
          "properties": {
            "category": {
              "type": "string",
              "description": "Category token from sv.tools.list_categories (e.g. 'messaging', 'directory', 'observability')."
            }
          }
        }
        """);

    /// <summary>
    /// Built-in category metadata. Each entry pairs the canonical token
    /// (the value tools stamp on <see cref="ToolDefinition.Category"/>)
    /// with a one-line summary surfaced by
    /// <see cref="ListCategoriesTool"/> and an extended
    /// usage-guidance string surfaced by <see cref="ListTool"/>.
    /// </summary>
    private static readonly IReadOnlyList<CategoryDescriptor> KnownCategories =
    [
        new(
            ToolCategories.Messaging,
            "Reply on the current thread or fan out to other participants.",
            "Use sv.messaging.send to reply on the thread you were called on, " +
            "or to send a new message to any addressable participant in your " +
            "tenant. Use sv.messaging.multicast to deliver the same message to " +
            "an explicit address list or a resolved scope (unit-members, " +
            "siblings). Delivery is one-way: each call returns a delivery " +
            "acknowledgement, not the recipient's reply (ADR-0049). A reply, " +
            "if any, arrives later as a separate message on the thread."),
        new(
            ToolCategories.Directory,
            "Look up agents, units, and humans by address, role, or expertise.",
            "Use sv.directory.lookup when you already know an address (for " +
            "example the sender of the inbound message) and need the entry's " +
            "role / expertise / status. Use sv.directory.list to enumerate " +
            "members of a unit, the caller's siblings, or peers matching a " +
            "role or expertise filter. Every entry carries enough to act on " +
            "(address, display name, role, expertise, advisory live status) — " +
            "feed an address back into sv.messaging.send to reach the entry."),
        new(
            ToolCategories.Observability,
            "Emit progress and decision signals operators can see live.",
            "Use sv.progress.report to publish a narrative progress beat with " +
            "an optional 0..1 fraction so a long-running turn is not silent " +
            "until completion. The platform records these as RuntimeProgress " +
            "activities visible in the portal and CLI live-tail."),
        new(
            ToolCategories.Tools,
            "Discover capability categories and their full tool definitions.",
            "Call sv.tools.list_categories on startup to see what your " +
            "tool surface contains beyond the fundamental core, then call " +
            "sv.tools.list(category) to retrieve the full tool definitions " +
            "(name + description + JSON input schema) and category-level " +
            "usage guidance for any category you need to act through."),
        new(
            ToolCategories.Memory,
            "Private memory and shared participant-set history.",
            "Two surfaces on the same category. Private memory: use " +
            "sv.memory.add to record long-term entries that survive across " +
            "conversations or short-term working notes scoped to a thread; " +
            "sv.memory.list / sv.memory.search to retrieve; " +
            "sv.memory.update / sv.memory.delete to mutate. Caller-scoped " +
            "— another agent's entries are not visible. Shared history: " +
            "sv.memory.engagements lists the participant sets you share a " +
            "timeline with (most-recent activity first); " +
            "sv.memory.history_with(participants=[…]) fetches the full " +
            "timeline shared with a participant set (your own address is " +
            "auto-included — do not list yourself); " +
            "sv.memory.search_messages free-text-searches across the " +
            "timelines you participate in, optionally scoped to a single " +
            "participant set. The agent never names a thread_id — the " +
            "participant set identifies the timeline."),
    ];

    private static readonly IReadOnlyDictionary<string, CategoryDescriptor> KnownCategoryIndex =
        KnownCategories.ToDictionary(c => c.Name, StringComparer.Ordinal);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    public SvToolsDiscoverySkillRegistry(
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = loggerFactory.CreateLogger<SvToolsDiscoverySkillRegistry>();
        _tools =
        [
            new ToolDefinition(
                ListCategoriesTool,
                "Enumerate the capability categories available to the calling agent " +
                "or unit. Output is a list of { name, description } entries — " +
                "name is the stable token to pass back into sv.tools.list, " +
                "description is a one-line summary of what the category contains. " +
                "Filtered to categories the caller has at least one granted tool in.",
                EmptyObjectSchema,
                ToolCategories.Tools),
            new ToolDefinition(
                ListTool,
                "Return the full tool definitions in a named category — one entry per " +
                "tool, each carrying { name, description, input_schema } plus a " +
                "category-level usage_guidance string. Use this to discover " +
                "additional capabilities beyond the fundamental core; once you " +
                "have called it, you have everything you need to invoke the " +
                "tools in that category — there is no separate describe step.",
                ListArgSchema,
                ToolCategories.Tools),
        ];
    }

    /// <inheritdoc />
    public string Name => "sv";

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools;

    /// <inheritdoc />
    public Task<JsonElement> InvokeAsync(
        string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
        throw new SpringException(
            $"Tool '{toolName}' on the {Name} tools-discovery registry requires caller context. " +
            "It is reachable only through the caller-aware ISkillRegistry.InvokeAsync overload " +
            "(invoked by the MCP server with the active session's identity).");

    /// <inheritdoc />
    public Task<JsonElement> InvokeAsync(
        string toolName,
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        return toolName switch
        {
            ListCategoriesTool => ListCategoriesAsync(context, cancellationToken),
            ListTool => ListAsync(arguments, context, cancellationToken),
            _ => throw new SkillNotFoundException(toolName),
        };
    }

    private async Task<JsonElement> ListCategoriesAsync(
        ToolCallContext context, CancellationToken ct)
    {
        var grants = await ResolveGrantsAsync(context, ct);
        var categoriesByName = await ResolveCategoryToolsAsync(grants, ct);

        var ordered = categoriesByName.Keys
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("categories");
            writer.WriteStartArray();
            foreach (var name in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("description", ResolveCategoryDescription(name));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private async Task<JsonElement> ListAsync(
        JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        var category = RequireStringArg(arguments, "category");

        var grants = await ResolveGrantsAsync(context, ct);
        var categoriesByName = await ResolveCategoryToolsAsync(grants, ct);

        if (!categoriesByName.TryGetValue(category, out var tools))
        {
            // No grants in the category, or the category itself is unknown.
            // Either way the caller sees an empty listing — leaking the
            // difference would expose categories the caller is not granted.
            tools = Array.Empty<ToolDefinition>();
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("category", category);
            writer.WriteString("usage_guidance", ResolveCategoryUsageGuidance(category));
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("input_schema");
                tool.InputSchema.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Resolves the caller's effective tool grants. Returns <c>null</c>
    /// when no <see cref="IToolGrantResolver"/> is registered (limited
    /// unit-test harnesses) — discovery then surfaces the full
    /// registered set, matching the MCP server's behaviour when no
    /// resolver is wired.
    /// </summary>
    private async Task<HashSet<string>?> ResolveGrantsAsync(
        ToolCallContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.CallerId)
            || !GuidFormatter.TryParse(context.CallerId, out var callerGuid))
        {
            throw new SpringException(
                $"sv.tools.* requires a caller id; the active MCP session did not supply one.");
        }

        var callerKind = string.IsNullOrWhiteSpace(context.CallerKind)
            ? Address.AgentScheme
            : context.CallerKind;
        var subject = new Address(callerKind, callerGuid);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetService<IToolGrantResolver>();
        if (resolver is null)
        {
            return null;
        }

        var effective = await resolver.ResolveAsync(subject, ct);
        return new HashSet<string>(
            effective.Select(t => t.Name),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Enumerates every registered <see cref="ToolDefinition"/> the
    /// caller has been granted and groups them by
    /// <see cref="ToolDefinition.Category"/>. Tools with an empty
    /// category are excluded from the listing — they remain reachable
    /// directly through MCP <c>tools/call</c> but do not participate
    /// in category-based discovery.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlyList<ToolDefinition>>> ResolveCategoryToolsAsync(
        HashSet<string>? grants, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var registries = scope.ServiceProvider.GetServices<ISkillRegistry>();

        var byCategory = new Dictionary<string, List<ToolDefinition>>(StringComparer.Ordinal);
        foreach (var registry in registries)
        {
            foreach (var tool in registry.GetToolDefinitions())
            {
                if (string.IsNullOrEmpty(tool.Category))
                {
                    continue;
                }
                if (grants is not null && !grants.Contains(tool.Name))
                {
                    continue;
                }
                if (!byCategory.TryGetValue(tool.Category, out var list))
                {
                    list = new List<ToolDefinition>();
                    byCategory[tool.Category] = list;
                }
                list.Add(tool);
            }
        }

        ct.ThrowIfCancellationRequested();

        return byCategory.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<ToolDefinition>)kvp.Value,
            StringComparer.Ordinal);
    }

    private static string ResolveCategoryDescription(string name) =>
        KnownCategoryIndex.TryGetValue(name, out var descriptor)
            ? descriptor.Description
            : string.Empty;

    private static string ResolveCategoryUsageGuidance(string name) =>
        KnownCategoryIndex.TryGetValue(name, out var descriptor)
            ? descriptor.UsageGuidance
            : string.Empty;

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

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Pairs a category token with the short summary
    /// <see cref="ListCategoriesTool"/> returns and the extended
    /// usage-guidance text <see cref="ListTool"/> returns.
    /// </summary>
    private sealed record CategoryDescriptor(
        string Name,
        string Description,
        string UsageGuidance);
}
