// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv;

using System.Text.Json;

using Cvoya.Spring.Connector.Arxiv.Skills;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// Registers arxiv connector tool definitions and dispatches invocations.
/// Implements <see cref="ISkillRegistry"/> so the MCP server and the platform
/// prompt-assembly path surface arxiv tools alongside every other connector.
///
/// The exported tool set intentionally includes <c>searchLiterature</c>: the
/// research package's <c>literature-review</c> skill bundle declares that
/// exact tool name, and resolving it through a concrete connector turns the
/// bundle's "referenced tool not present" validation warning into a no-op
/// on any unit that binds arxiv.
/// </summary>
public class ArxivSkillRegistry : ISkillRegistry
{
    private readonly SearchLiteratureSkill _search;
    private readonly FetchAbstractSkill _fetch;
    private readonly ILogger<ArxivSkillRegistry> _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    /// <summary>
    /// Creates the registry with the arxiv-backed skill implementations.
    /// </summary>
    public ArxivSkillRegistry(
        SearchLiteratureSkill search,
        FetchAbstractSkill fetch,
        ILoggerFactory loggerFactory)
    {
        _search = search;
        _fetch = fetch;
        _logger = loggerFactory.CreateLogger<ArxivSkillRegistry>();
        _tools = BuildToolDefinitions();
    }

    /// <inheritdoc />
    public string Name => "arxiv";

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools;

    /// <inheritdoc />
    public async Task<JsonElement> InvokeAsync(
        string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Invoking arxiv skill {ToolName}", toolName);

        return toolName switch
        {
            "searchLiterature" => await _search.ExecuteAsync(
                GetString(arguments, "query")
                    ?? throw new ArgumentException("'query' is required.", nameof(arguments)),
                GetStringArray(arguments, "categories"),
                GetInt(arguments, "yearFrom"),
                GetInt(arguments, "yearTo"),
                GetInt(arguments, "limit") ?? 20,
                cancellationToken),
            "fetchAbstract" => await _fetch.ExecuteAsync(
                GetString(arguments, "arxivId")
                    ?? throw new ArgumentException("'arxivId' is required.", nameof(arguments)),
                cancellationToken),
            _ => throw new SkillNotFoundException(toolName),
        };
    }

    private static IReadOnlyList<ToolDefinition> BuildToolDefinitions()
    {
        return new[]
        {
            ToolDef(
                "searchLiterature",
                "Search the arxiv preprint catalogue for papers matching the supplied query, optionally scoped to arxiv categories and a publication-year window.",
                new
                {
                    type = "object",
                    required = new[] { "query" },
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            description = "Search query — plain text or arxiv search-query operators.",
                        },
                        categories = new
                        {
                            type = "array",
                            items = new { type = "string" },
                            description = "Optional arxiv categories to scope the search to (e.g. cs.AI, stat.ML).",
                        },
                        yearFrom = new
                        {
                            type = "integer",
                            description = "Optional inclusive lower bound on publication year.",
                        },
                        yearTo = new
                        {
                            type = "integer",
                            description = "Optional inclusive upper bound on publication year.",
                        },
                        limit = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum = 100,
                            description = "Maximum number of results to return (default 20, hard cap 100).",
                        },
                    },
                }),
            ToolDef(
                "fetchAbstract",
                "Fetch the full abstract and metadata for a single arxiv entry by its canonical id (e.g. 2401.12345).",
                new
                {
                    type = "object",
                    required = new[] { "arxivId" },
                    properties = new
                    {
                        arxivId = new
                        {
                            type = "string",
                            description = "The canonical arxiv id without the 'arXiv:' prefix or version suffix.",
                        },
                    },
                }),
        };
    }

    private static ToolDefinition ToolDef(string name, string description, object schema)
        => new(name, description, JsonSerializer.SerializeToElement(schema));

    private static string? GetString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return null;
    }

    private static int? GetInt(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var i))
        {
            return i;
        }
        return null;
    }

    private static IReadOnlyList<string>? GetStringArray(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
                .ToArray();
        }
        return null;
    }
}