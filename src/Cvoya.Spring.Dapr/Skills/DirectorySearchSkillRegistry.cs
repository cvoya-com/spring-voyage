// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// Meta-skill registry that advertises <c>directory/search</c> (#542) so a
/// planner (or any <see cref="ISkillInvoker"/> consumer) can resolve a
/// capability description to concrete <c>expertise/{slug}</c> hits BEFORE
/// invoking any other skill. This is load-bearing for PR #541's practical
/// usefulness — without it the planner has no way to go from "refactor this
/// Python" to the right slug.
/// </summary>
/// <remarks>
/// <para>
/// The skill is exposed through <see cref="ISkillRegistry"/> (same seam the
/// GitHub connector and the expertise-directory-driven catalog use) so MCP
/// <c>tools/list</c>, the <c>/api/v1/skills</c> endpoint, and any future
/// planner see it without special-casing. Invocation delegates to the
/// registered <see cref="IExpertiseSearch"/> — the OSS default is the
/// in-memory lexical implementation; the private cloud repo can swap in a
/// Postgres-FTS-backed store without touching this registry.
/// </para>
/// <para>
/// <b>Caller context.</b> The registry itself has no access to the caller
/// identity because <see cref="ISkillRegistry.InvokeAsync"/> is caller-
/// agnostic. That's fine: the query body carries an optional
/// <c>caller</c> field, and the boundary rules on the search path apply
/// from there. Consumers that route through <see cref="ISkillInvoker"/>
/// pass the caller on the envelope instead; an alternative invoker that
/// wants boundary-specific search results can resolve
/// <see cref="IExpertiseSearch"/> directly and bypass this adapter.
/// </para>
/// </remarks>
public class DirectorySearchSkillRegistry : ISkillRegistry
{
    /// <summary>Catalog name for the meta-skill.</summary>
    public const string SkillName = "directory/search";

    private static readonly JsonElement InputSchema = BuildInputSchema();
    private static readonly JsonElement OutputSchema = BuildOutputSchema();

    private readonly IExpertiseSearch _search;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    /// <summary>
    /// Builds the registry with the supplied search implementation.
    /// </summary>
    public DirectorySearchSkillRegistry(IExpertiseSearch search, ILoggerFactory loggerFactory)
    {
        _search = search;
        _logger = loggerFactory.CreateLogger<DirectorySearchSkillRegistry>();
        _tools = new[]
        {
            new ToolDefinition(
                SkillName,
                "Search the expertise directory by free-text query or structured filters. " +
                "Returns a ranked list of hits — each hit carries the capability slug that can " +
                "then be invoked as 'expertise/{slug}'.",
                InputSchema),
        };
    }

    /// <inheritdoc />
    public string Name => "directory";

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools;

    /// <summary>
    /// Published JSON Schema for the tool's output. Exposed as a static so
    /// the portal and external clients can reference the same schema shape
    /// the MCP surface advertises.
    /// </summary>
    public static JsonElement GetOutputSchema() => OutputSchema;

    /// <inheritdoc />
    public async Task<JsonElement> InvokeAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(toolName, SkillName, StringComparison.Ordinal))
        {
            throw new SkillNotFoundException(toolName);
        }

        var query = ParseArguments(arguments);
        var result = await _search.SearchAsync(query, cancellationToken);

        return BuildResultPayload(result);
    }

    internal static ExpertiseSearchQuery ParseArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return new ExpertiseSearchQuery();
        }

        string? text = null;
        Address? owner = null;
        List<string>? domains = null;
        bool typedOnly = false;
        bool insideUnit = false;
        Address? caller = null;
        int limit = ExpertiseSearchQuery.DefaultLimit;
        int offset = 0;

        foreach (var prop in arguments.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "text":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        text = prop.Value.GetString();
                    }
                    break;
                case "owner":
                    owner = ParseAddress(prop.Value);
                    break;
                case "caller":
                    caller = ParseAddress(prop.Value);
                    break;
                case "domains":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        domains = new List<string>();
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                var value = item.GetString();
                                if (!string.IsNullOrWhiteSpace(value))
                                {
                                    domains.Add(value);
                                }
                            }
                        }
                    }
                    break;
                case "typedOnly":
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        typedOnly = prop.Value.GetBoolean();
                    }
                    break;
                case "insideUnit":
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        insideUnit = prop.Value.GetBoolean();
                    }
                    break;
                case "limit":
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var l))
                    {
                        limit = l;
                    }
                    break;
                case "offset":
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var o))
                    {
                        offset = o;
                    }
                    break;
            }
        }

        return new ExpertiseSearchQuery(
            Text: text,
            Owner: owner,
            Domains: domains,
            TypedOnly: typedOnly,
            Caller: caller,
            Context: insideUnit ? BoundaryViewContext.InsideUnit : BoundaryViewContext.External,
            Limit: limit,
            Offset: offset);
    }

    private static Address? ParseAddress(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            // Accept the wire shape "scheme://path" that CLI and planner
            // callers naturally type.
            var raw = element.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }
            var sep = raw.IndexOf("://", StringComparison.Ordinal);
            if (sep <= 0 || sep >= raw.Length - 3)
            {
                return null;
            }
            return new Address(raw[..sep], raw[(sep + 3)..]);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            string? scheme = null;
            string? path = null;
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, "scheme", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String)
                {
                    scheme = prop.Value.GetString();
                }
                else if (string.Equals(prop.Name, "path", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String)
                {
                    path = prop.Value.GetString();
                }
            }
            if (!string.IsNullOrWhiteSpace(scheme) && !string.IsNullOrWhiteSpace(path))
            {
                return new Address(scheme!, path!);
            }
        }

        return null;
    }

    internal static JsonElement BuildResultPayload(ExpertiseSearchResult result)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("totalCount", result.TotalCount);
            writer.WriteNumber("limit", result.Limit);
            writer.WriteNumber("offset", result.Offset);
            writer.WritePropertyName("hits");
            writer.WriteStartArray();
            foreach (var hit in result.Hits)
            {
                writer.WriteStartObject();
                writer.WriteString("slug", hit.Slug);
                writer.WriteString("skill", ExpertiseSkillNaming.Prefix + hit.Slug);
                writer.WriteString("name", hit.Domain.Name);
                writer.WriteString("description", hit.Domain.Description ?? string.Empty);
                if (hit.Domain.Level is { } level)
                {
                    writer.WriteString("level", level.ToString().ToLowerInvariant());
                }
                writer.WriteString("owner", $"{hit.Owner.Scheme}://{hit.Owner.Path}");
                writer.WriteString("ownerDisplayName", hit.OwnerDisplayName ?? string.Empty);
                if (hit.AggregatingUnit is { } unit)
                {
                    writer.WriteString("aggregatingUnit", $"{unit.Scheme}://{unit.Path}");
                }
                writer.WriteBoolean("typedContract", hit.TypedContract);
                writer.WriteNumber("score", hit.Score);
                writer.WriteString("matchReason", hit.MatchReason);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var bytes = stream.ToArray();
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    private static JsonElement BuildInputSchema()
    {
        const string schemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "text": {
              "type": "string",
              "description": "Free-text query matched against slug, display name, description, and tags."
            },
            "owner": {
              "oneOf": [
                { "type": "string", "description": "Address in 'scheme://path' wire form." },
                {
                  "type": "object",
                  "properties": {
                    "scheme": { "type": "string" },
                    "path": { "type": "string" }
                  },
                  "required": ["scheme", "path"]
                }
              ],
              "description": "Optional owner filter."
            },
            "domains": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional list of domain names or slugs to restrict the result set."
            },
            "typedOnly": {
              "type": "boolean",
              "default": false,
              "description": "When true, only typed-contract (skill-callable) entries surface."
            },
            "insideUnit": {
              "type": "boolean",
              "default": false,
              "description": "When true, request the inside-the-unit boundary view (full scope)."
            },
            "caller": {
              "oneOf": [
                { "type": "string" },
                {
                  "type": "object",
                  "properties": {
                    "scheme": { "type": "string" },
                    "path": { "type": "string" }
                  },
                  "required": ["scheme", "path"]
                }
              ],
              "description": "Optional caller address for boundary scoping."
            },
            "limit": { "type": "integer", "minimum": 0, "default": 50 },
            "offset": { "type": "integer", "minimum": 0, "default": 0 }
          }
        }
        """;
        using var doc = JsonDocument.Parse(schemaJson);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildOutputSchema()
    {
        const string schemaJson = """
        {
          "type": "object",
          "properties": {
            "totalCount": { "type": "integer" },
            "limit": { "type": "integer" },
            "offset": { "type": "integer" },
            "hits": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "slug": { "type": "string" },
                  "skill": { "type": "string", "description": "Invocable skill name — expertise/{slug}." },
                  "name": { "type": "string" },
                  "description": { "type": "string" },
                  "level": { "type": "string" },
                  "owner": { "type": "string" },
                  "ownerDisplayName": { "type": "string" },
                  "aggregatingUnit": { "type": "string" },
                  "typedContract": { "type": "boolean" },
                  "score": { "type": "number" },
                  "matchReason": { "type": "string" }
                },
                "required": ["slug", "skill", "name", "owner", "typedContract", "score", "matchReason"]
              }
            }
          },
          "required": ["totalCount", "limit", "offset", "hits"]
        }
        """;
        using var doc = JsonDocument.Parse(schemaJson);
        return doc.RootElement.Clone();
    }
}