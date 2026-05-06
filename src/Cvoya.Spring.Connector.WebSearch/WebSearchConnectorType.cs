// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Web-search concrete implementation of <see cref="IConnectorType"/>. Sits
/// behind the pluggable <see cref="IWebSearchProvider"/> abstraction so
/// different search backends can be slotted in without changing the connector
/// surface. The per-unit config never stores an API-key in plaintext — it
/// holds a secret-name reference that the skill layer resolves through
/// <see cref="Cvoya.Spring.Core.Secrets.ISecretResolver"/> at invoke time.
/// </summary>
public class WebSearchConnectorType : IConnectorType
{
    /// <summary>
    /// The stable identity persisted on every unit binding.
    /// </summary>
    public static readonly Guid WebSearchTypeId =
        new("f7c4d2e9-7b90-4f30-90e1-2b2df3a1c7a2");

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);

    private readonly IUnitConnectorConfigStore _configStore;
    private readonly IEnumerable<IWebSearchProvider> _providers;
    private readonly WebSearchConnectorOptions _options;
    private readonly ILogger<WebSearchConnectorType> _logger;

    /// <summary>
    /// Creates the connector type. <paramref name="providers"/> is the set of
    /// registered <see cref="IWebSearchProvider"/> implementations — the
    /// config PUT rejects bindings whose <c>provider</c> is not in this set.
    /// </summary>
    public WebSearchConnectorType(
        IUnitConnectorConfigStore configStore,
        IEnumerable<IWebSearchProvider> providers,
        IOptions<WebSearchConnectorOptions> options,
        ILoggerFactory loggerFactory)
    {
        _configStore = configStore;
        _providers = providers;
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<WebSearchConnectorType>();
    }

    /// <inheritdoc />
    public Guid TypeId => WebSearchTypeId;

    /// <inheritdoc />
    public string Slug => "web-search";

    /// <inheritdoc />
    public string DisplayName => "Web Search";

    /// <inheritdoc />
    public string Description => "Generic web-search façade over a pluggable provider (Brave by default; Bing, Google Custom Search, or SearxNG can be slotted in).";

    /// <inheritdoc />
    public Type ConfigType => typeof(UnitWebSearchConfig);

    /// <inheritdoc />
    public void MapRoutes(IEndpointRouteBuilder group)
    {
        group.MapGet("/units/{unitId}/config", GetConfigAsync)
            .WithName("GetUnitWebSearchConnectorConfig")
            .WithSummary("Get the web-search connector config bound to a unit")
            .WithTags("Connectors.WebSearch")
            .Produces<UnitWebSearchConfigResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/units/{unitId}/config", PutConfigAsync)
            .WithName("PutUnitWebSearchConnectorConfig")
            .WithSummary("Bind a unit to the web-search connector and upsert its config")
            .WithTags("Connectors.WebSearch")
            .Accepts<UnitWebSearchConfigRequest>("application/json")
            .Produces<UnitWebSearchConfigResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/actions/providers", ListProvidersAsync)
            .WithName("ListWebSearchProviders")
            .WithSummary("List the web-search providers currently registered in this host")
            .WithTags("Connectors.WebSearch")
            .Produces<WebSearchProviderDescriptor[]>(StatusCodes.Status200OK);

        group.MapGet("/config-schema", GetConfigSchemaEndpointAsync)
            .WithName("GetWebSearchConnectorConfigSchema")
            .WithSummary("Get the JSON Schema describing the web-search connector config body")
            .WithTags("Connectors.WebSearch")
            .Produces<JsonElement>(StatusCodes.Status200OK);
    }

    /// <inheritdoc />
    public Task<JsonElement?> GetConfigSchemaAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<JsonElement?>(BuildConfigSchema(_providers));

    /// <inheritdoc />
    public Task OnUnitStartingAsync(string unitId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("web-search connector unit start is a no-op (no external resources); unit={UnitId}", unitId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnUnitStoppingAsync(string unitId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("web-search connector unit stop is a no-op (no external resources); unit={UnitId}", unitId);
        return Task.CompletedTask;
    }

    // #1748: store calls below pass the unit's actor-Guid form because the
    // default UnitActorConnectorConfigStore routes through Address.For
    // internally. Resolution is best-effort — a non-Guid route value or a
    // missing directory entry falls through unchanged so the legacy
    // opaque-string callers (test fixtures) keep working.
    private static async Task<string> ResolveUnitActorIdAsync(
        IDirectoryService directory, string unitId, CancellationToken ct)
    {
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitId, out _))
        {
            return unitId;
        }
        var entry = await directory.ResolveAsync(Address.For("unit", unitId), ct);
        return entry is null
            ? unitId
            : Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
    }

    private async Task<IResult> GetConfigAsync(
        string unitId,
        [FromServices] IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var actorId = await ResolveUnitActorIdAsync(directoryService, unitId, cancellationToken);
        var binding = await _configStore.GetAsync(actorId, cancellationToken);
        if (binding is null || binding.TypeId != WebSearchTypeId)
        {
            return Results.Problem(
                detail: $"Unit '{unitId}' is not bound to the web-search connector.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var config = binding.Config.Deserialize<UnitWebSearchConfig>(ConfigJson);
        if (config is null)
        {
            return Results.Problem(
                detail: $"Stored config for unit '{unitId}' is not web-search-shaped.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(ToResponse(unitId, config));
    }

    private async Task<IResult> PutConfigAsync(
        string unitId,
        [FromBody] UnitWebSearchConfigRequest request,
        [FromServices] IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Provider))
        {
            return Results.Problem(
                detail: "'provider' is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var knownProviderIds = _providers.Select(p => p.Id).ToArray();
        if (!knownProviderIds.Any(id =>
            string.Equals(id, request.Provider, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Problem(
                detail: $"Unknown web-search provider '{request.Provider}'. Registered providers: {string.Join(", ", knownProviderIds)}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var maxResults = request.MaxResults ?? _options.DefaultMaxResults;
        if (maxResults <= 0)
        {
            return Results.Problem(
                detail: "'maxResults' must be a positive integer.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actorId = await ResolveUnitActorIdAsync(directoryService, unitId, cancellationToken);
        var config = new UnitWebSearchConfig(
            Provider: request.Provider.ToLowerInvariant(),
            ApiKeySecretName: string.IsNullOrWhiteSpace(request.ApiKeySecretName) ? null : request.ApiKeySecretName,
            MaxResults: Math.Clamp(maxResults, 1, 50),
            Safesearch: request.Safesearch ?? true);

        var payload = JsonSerializer.SerializeToElement(config, ConfigJson);
        await _configStore.SetAsync(actorId, WebSearchTypeId, payload, cancellationToken);

        return Results.Ok(ToResponse(unitId, config));
    }

    private Task<IResult> ListProvidersAsync()
    {
        var descriptors = _providers
            .Select(p => new WebSearchProviderDescriptor(p.Id, p.DisplayName))
            .ToArray();
        return Task.FromResult(Results.Ok(descriptors));
    }

    private IResult GetConfigSchemaEndpointAsync()
    {
        return Results.Ok(BuildConfigSchema(_providers));
    }

    private static UnitWebSearchConfigResponse ToResponse(string unitId, UnitWebSearchConfig config)
        => new(unitId, config.Provider, config.ApiKeySecretName, config.MaxResults, config.Safesearch);

    // Hand-authored schema — the provider enum is computed from the registered
    // providers at call time so adding a new provider flows through to the
    // config form with no additional wiring.
    internal static JsonElement BuildConfigSchema(IEnumerable<IWebSearchProvider> providers)
    {
        var providerEnum = providers.Select(p => p.Id).DefaultIfEmpty("brave").Distinct().ToArray();

        var payload = new
        {
            schema = "https://json-schema.org/draft/2020-12/schema",
            title = "UnitWebSearchConfigRequest",
            type = "object",
            required = new[] { "provider" },
            properties = new
            {
                provider = new
                {
                    type = "string",
                    @enum = providerEnum,
                    description = "The provider id — must match a registered IWebSearchProvider.",
                },
                apiKeySecretName = new
                {
                    type = new[] { "string", "null" },
                    description = "Unit-scoped secret name holding the provider's API key. Null only if the provider needs no auth. Plaintext is never persisted here.",
                },
                maxResults = new
                {
                    type = new[] { "integer", "null" },
                    minimum = 1,
                    maximum = 50,
                    description = "Default result cap (null falls back to 10, hard cap 50).",
                },
                safesearch = new
                {
                    type = new[] { "boolean", "null" },
                    description = "Whether to enable the provider's safe-search filter (default true).",
                },
            },
        };

        // The anonymous-object serializer cannot emit '$schema' (dollar sign is
        // not a valid C# identifier), so swap it in post-hoc.
        var element = JsonSerializer.SerializeToElement(payload);
        var obj = element.Deserialize<Dictionary<string, object?>>();
        if (obj is not null && obj.Remove("schema"))
        {
            obj["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        }
        return JsonSerializer.SerializeToElement(obj);
    }
}