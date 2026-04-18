// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv;

using System.Text.Json;

using Cvoya.Spring.Connectors;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// arxiv concrete implementation of <see cref="IConnectorType"/>. The arxiv
/// API is read-only and unauthenticated; this connector therefore only exposes
/// a typed per-unit config (default categories + max results) and two
/// read-only skills (<c>searchLiterature</c>, <c>fetchAbstract</c>). There are
/// no webhooks, no secrets, and no unit lifecycle side-effects — the
/// lifecycle hooks are intentional no-ops.
/// </summary>
public class ArxivConnectorType : IConnectorType
{
    /// <summary>
    /// The stable identity persisted on every unit binding. Changing this
    /// value invalidates existing bindings — never change it in place.
    /// </summary>
    public static readonly Guid ArxivTypeId =
        new("b3c2f5a1-1d38-4a56-8c18-9ac8b2b2d401");

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);

    private readonly IUnitConnectorConfigStore _configStore;
    private readonly ArxivConnectorOptions _options;
    private readonly ILogger<ArxivConnectorType> _logger;

    /// <summary>
    /// Creates a new <see cref="ArxivConnectorType"/>.
    /// </summary>
    public ArxivConnectorType(
        IUnitConnectorConfigStore configStore,
        Microsoft.Extensions.Options.IOptions<ArxivConnectorOptions> options,
        ILoggerFactory loggerFactory)
    {
        _configStore = configStore;
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<ArxivConnectorType>();
    }

    /// <inheritdoc />
    public Guid TypeId => ArxivTypeId;

    /// <inheritdoc />
    public string Slug => "arxiv";

    /// <inheritdoc />
    public string DisplayName => "arxiv";

    /// <inheritdoc />
    public string Description => "Read-only arxiv search and abstract fetch for research units. No write operations, no authentication required.";

    /// <inheritdoc />
    public Type ConfigType => typeof(UnitArxivConfig);

    /// <inheritdoc />
    public void MapRoutes(IEndpointRouteBuilder group)
    {
        group.MapGet("/units/{unitId}/config", GetConfigAsync)
            .WithName("GetUnitArxivConnectorConfig")
            .WithSummary("Get the arxiv connector config bound to a unit")
            .WithTags("Connectors.Arxiv")
            .Produces<UnitArxivConfigResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/units/{unitId}/config", PutConfigAsync)
            .WithName("PutUnitArxivConnectorConfig")
            .WithSummary("Bind a unit to arxiv and upsert its per-unit config")
            .WithTags("Connectors.Arxiv")
            .Accepts<UnitArxivConfigRequest>("application/json")
            .Produces<UnitArxivConfigResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/config-schema", GetConfigSchemaEndpointAsync)
            .WithName("GetArxivConnectorConfigSchema")
            .WithSummary("Get the JSON Schema describing the arxiv connector config body")
            .WithTags("Connectors.Arxiv")
            .Produces<JsonElement>(StatusCodes.Status200OK);
    }

    /// <inheritdoc />
    public Task<JsonElement?> GetConfigSchemaAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<JsonElement?>(BuildConfigSchema());

    /// <inheritdoc />
    public Task OnUnitStartingAsync(string unitId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("arxiv connector unit start is a no-op (no external resources); unit={UnitId}", unitId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnUnitStoppingAsync(string unitId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("arxiv connector unit stop is a no-op (no external resources); unit={UnitId}", unitId);
        return Task.CompletedTask;
    }

    private async Task<IResult> GetConfigAsync(
        string unitId, CancellationToken cancellationToken)
    {
        var binding = await _configStore.GetAsync(unitId, cancellationToken);
        if (binding is null || binding.TypeId != ArxivTypeId)
        {
            return Results.Problem(
                detail: $"Unit '{unitId}' is not bound to the arxiv connector.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var config = binding.Config.Deserialize<UnitArxivConfig>(ConfigJson);
        if (config is null)
        {
            return Results.Problem(
                detail: $"Stored config for unit '{unitId}' is not arxiv-shaped.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(ToResponse(unitId, config));
    }

    private async Task<IResult> PutConfigAsync(
        string unitId,
        [FromBody] UnitArxivConfigRequest request,
        CancellationToken cancellationToken)
    {
        var maxResults = request.MaxResults ?? _options.DefaultMaxResults;
        if (maxResults <= 0)
        {
            return Results.Problem(
                detail: "'maxResults' must be a positive integer.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var config = new UnitArxivConfig(
            DefaultCategories: request.DefaultCategories,
            MaxResults: Math.Clamp(maxResults, 1, 100));

        var payload = JsonSerializer.SerializeToElement(config, ConfigJson);
        await _configStore.SetAsync(unitId, ArxivTypeId, payload, cancellationToken);

        return Results.Ok(ToResponse(unitId, config));
    }

    private static IResult GetConfigSchemaEndpointAsync()
    {
        return Results.Ok(BuildConfigSchema());
    }

    private static UnitArxivConfigResponse ToResponse(string unitId, UnitArxivConfig config)
        => new(
            unitId,
            config.DefaultCategories ?? Array.Empty<string>(),
            config.MaxResults);

    // Hand-authored schema mirroring UnitArxivConfigRequest. Kept tiny; the
    // config surface is deliberately minimal because arxiv has no auth layer
    // to configure.
    internal static JsonElement BuildConfigSchema()
    {
        const string schema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "UnitArxivConfigRequest",
          "type": "object",
          "properties": {
            "defaultCategories": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Default arxiv categories (e.g. cs.AI, cs.LG) applied when the caller does not specify one. Null or empty means no category filter."
            },
            "maxResults": {
              "type": ["integer", "null"],
              "minimum": 1,
              "maximum": 100,
              "description": "Default result cap for searchLiterature calls. Null falls back to the connector default (20)."
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(schema);
        return doc.RootElement.Clone();
    }
}