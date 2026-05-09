// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch.Skills;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Executes a web search on behalf of a unit, looking up that unit's binding
/// to select the provider and resolve the unit-scoped API-key secret just
/// before dispatch. Secret plaintext never appears in logs — only the secret
/// reference and whether the resolve path was direct / inherited / not found.
/// </summary>
public class WebSearchSkill
{
    private readonly IUnitConnectorConfigStore _configStore;
    private readonly IEnumerable<IWebSearchProvider> _providers;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WebSearchConnectorOptions _options;
    private readonly ILogger<WebSearchSkill> _logger;

    /// <summary>
    /// Creates the skill. The skill itself is registered as a singleton so it
    /// can be consumed by the singleton <see cref="WebSearchSkillRegistry"/>;
    /// scoped dependencies (<see cref="ISecretResolver"/>,
    /// <see cref="ITenantContext"/>) are resolved per-call through
    /// <see cref="IServiceScopeFactory"/> so DI audit decorators still see
    /// every secret resolve.
    /// </summary>
    public WebSearchSkill(
        IUnitConnectorConfigStore configStore,
        IEnumerable<IWebSearchProvider> providers,
        IServiceScopeFactory scopeFactory,
        IOptions<WebSearchConnectorOptions> options,
        ILoggerFactory loggerFactory)
    {
        _configStore = configStore;
        _providers = providers;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<WebSearchSkill>();
    }

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs a web search.
    /// </summary>
    /// <param name="unitId">
    /// The unit whose binding picks the provider and holds the secret. The
    /// skill looks up the binding on every call so a unit that rebinds mid-run
    /// picks up the new provider without needing a registry rebuild.
    /// </param>
    /// <param name="query">The search query.</param>
    /// <param name="limit">Maximum number of results. Hard-capped at 50.</param>
    /// <param name="safesearch">
    /// Whether to enable safe-search. <c>null</c> falls back to the unit's
    /// persisted default; unset defaults to <c>true</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonElement> ExecuteAsync(
        string unitId,
        string query,
        int? limit,
        bool? safesearch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var config = await LoadConfigAsync(unitId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Unit '{unitId}' is not bound to the web-search connector.");

        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.Id, config.Provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"web-search provider '{config.Provider}' is not registered. "
                + "Register an IWebSearchProvider implementation with this id, "
                + "or rebind the unit to a supported provider.");

        string? apiKey = null;
        Guid tenantId = Guid.Empty;
        Guid? unitOwnerId = Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitId, out var parsedUnitId)
            ? parsedUnitId
            : null;
        using (var scope = _scopeFactory.CreateScope())
        {
            tenantId = scope.ServiceProvider.GetRequiredService<ITenantContext>().CurrentTenantId;
            if (!string.IsNullOrWhiteSpace(config.ApiKeySecretName))
            {
                var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();
                var secretRef = new SecretRef(
                    SecretScope.Unit, unitOwnerId, config.ApiKeySecretName);
                var resolution = await resolver.ResolveWithPathAsync(secretRef, cancellationToken);
                // Log only the structural bits. Value is never logged.
                _logger.LogInformation(
                    "Resolved web-search API key for unit {UnitId} secret={SecretName} path={Path}",
                    unitId, config.ApiKeySecretName, resolution.Path);
                apiKey = resolution.Value;
                if (string.IsNullOrEmpty(apiKey))
                {
                    throw new InvalidOperationException(
                        $"Web-search secret '{config.ApiKeySecretName}' for unit '{unitId}' "
                        + "is not available (no matching registry entry or empty value).");
                }
            }
        }

        var cap = Math.Clamp(limit ?? config.MaxResults, 1, 50);
        var request = new WebSearchRequest(
            Query: query,
            Limit: cap,
            Safesearch: safesearch ?? config.Safesearch,
            ApiKey: apiKey);

        var results = await provider.SearchAsync(request, cancellationToken);

        return JsonSerializer.SerializeToElement(new
        {
            provider = provider.Id,
            results = results.Select(r => new
            {
                title = r.Title,
                url = r.Url,
                snippet = r.Snippet,
                source = r.Source,
            }).ToArray(),
            count = results.Count,
            tenantId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(tenantId),
        });
    }

    private async Task<UnitWebSearchConfig?> LoadConfigAsync(string unitId, CancellationToken ct)
    {
        var binding = await _configStore.GetAsync(unitId, ct);
        if (binding is null || binding.TypeId != WebSearchConnectorType.WebSearchTypeId)
        {
            return null;
        }
        try
        {
            var config = binding.Config.Deserialize<UnitWebSearchConfig>(ConfigJson);
            if (config is null) return null;
            if (string.IsNullOrWhiteSpace(config.Provider))
            {
                return config with { Provider = _options.DefaultProvider };
            }
            return config;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Unit {UnitId} web-search config could not be deserialized.", unitId);
            return null;
        }
    }
}
