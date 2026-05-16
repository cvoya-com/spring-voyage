// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch;

using System.Text.Json;

using Cvoya.Spring.Connector.WebSearch.Skills;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// Registers the web-search tool definition and dispatches invocations through
/// the pluggable <see cref="IWebSearchProvider"/> layer. The tool shape stays
/// the same regardless of which provider the unit selected, so agents can use
/// it uniformly without knowing about the backend.
/// </summary>
public class WebSearchSkillRegistry : ISkillRegistry
{
    private readonly WebSearchSkill _skill;
    private readonly ILogger<WebSearchSkillRegistry> _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    /// <summary>
    /// Creates the registry.
    /// </summary>
    public WebSearchSkillRegistry(WebSearchSkill skill, ILoggerFactory loggerFactory)
    {
        _skill = skill;
        _logger = loggerFactory.CreateLogger<WebSearchSkillRegistry>();
        _tools = BuildToolDefinitions();
    }

    /// <inheritdoc />
    public string Name => "web-search";

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools;

    /// <inheritdoc />
    public async Task<JsonElement> InvokeAsync(
        string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Invoking web-search skill {ToolName}", toolName);

        return toolName switch
        {
            "websearch.search" => await _skill.ExecuteAsync(
                GetString(arguments, "unitId")
                    ?? throw new ArgumentException("'unitId' is required.", nameof(arguments)),
                GetString(arguments, "query")
                    ?? throw new ArgumentException("'query' is required.", nameof(arguments)),
                GetInt(arguments, "limit"),
                GetBool(arguments, "safesearch"),
                cancellationToken),
            _ => throw new SkillNotFoundException(toolName),
        };
    }

    private static IReadOnlyList<ToolDefinition> BuildToolDefinitions()
    {
        return new[]
        {
            ToolDef(
                "websearch.search",
                "Run a general-purpose web search via the unit's configured provider and return the top results.",
                new
                {
                    type = "object",
                    required = new[] { "unitId", "query" },
                    properties = new
                    {
                        unitId = new
                        {
                            type = "string",
                            description = "The id of the unit whose web-search binding should be used. The binding selects the provider and the unit-scoped API-key secret.",
                        },
                        query = new
                        {
                            type = "string",
                            description = "Search query string.",
                        },
                        limit = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum = 50,
                            description = "Maximum number of results to return (default per-unit; hard cap 50).",
                        },
                        safesearch = new
                        {
                            type = "boolean",
                            description = "Override the per-unit safe-search flag for this call only.",
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

    private static bool? GetBool(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => (bool?)null,
            };
        }
        return null;
    }
}
