// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="IModelCatalog"/> implementation that dynamically fetches
/// model lists from provider endpoints when one exists (Anthropic, OpenAI,
/// Ollama) and falls back to a curated static list otherwise. Results are
/// cached in-memory per provider with a short TTL (<see cref="CacheTtl"/>) so
/// the wizard doesn't hit the provider on every page render. Scoped to the
/// UI's model-picker dropdown — see #597.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton. The cache is per-host; restarting the API host
/// re-fetches on the next call. Cache keys are the provider id so tenant-
/// scoped overrides (injected via DI by the private cloud host) get their own
/// catalog instance and thus their own cache.
/// </para>
/// <para>
/// Failure modes:
/// <list type="bullet">
///   <item>no configured API key → skip the fetch, log once, return static list;</item>
///   <item>HTTP 401/403 → log once, return static list (and don't retry until TTL);</item>
///   <item>HTTP 429 / 5xx / network → log once, return static list (cached);</item>
///   <item>provider without a known models endpoint (<c>google</c>) → always static.</item>
/// </list>
/// Caching the fallback means a single provider outage doesn't hammer the
/// endpoint on every wizard open; the fetch retries after the TTL expires.
/// </para>
/// </remarks>
public class ModelCatalog : IModelCatalog
{
    /// <summary>How long a successful or failed fetch stays cached.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    // Anthropic's public models endpoint. Keep the list in `ClaudeFallback` in
    // sync with the most-recent generally-available families so a fresh install
    // without an API key still shows something plausible.
    private const string AnthropicBaseUrl = "https://api.anthropic.com";
    private const string AnthropicVersion = "2023-06-01";
    private const string OpenAiBaseUrl = "https://api.openai.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<AiProviderOptions> _anthropicOptions;
    private readonly IOptions<OllamaOptions> _ollamaOptions;
    private readonly Func<CancellationToken, Task<LlmCredentialResolution>> _resolveAnthropicCredential;
    private readonly Func<CancellationToken, Task<LlmCredentialResolution>> _resolveOpenAiCredential;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ModelCatalog> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Static fallback lists used when a provider has no models endpoint or
    /// when the dynamic fetch fails. Order-sensitive — the wizard seeds the
    /// dropdown default from the first entry.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> StaticFallback =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new[]
            {
                // Keep the first entry in sync with AiProviderOptions.Model — the
                // wizard seeds the model field from this list when no dynamic
                // fetch is available, and the platform default must match.
                "claude-sonnet-4-20250514",
                "claude-opus-4-20250514",
                "claude-haiku-4-20250514",
            },
            ["openai"] = new[] { "gpt-4o", "gpt-4o-mini", "o3-mini" },
            ["google"] = new[] { "gemini-2.5-pro", "gemini-2.5-flash" },
            ["ollama"] = new[]
            {
                "qwen2.5:14b",
                "llama3.2:3b",
                "llama3.1:8b",
                "mistral:7b",
                "deepseek-coder-v2:16b",
            },
        };

    /// <summary>
    /// Constructs the catalog.
    /// </summary>
    /// <param name="httpClientFactory">Factory for the outbound HTTP clients used to talk to provider endpoints.</param>
    /// <param name="anthropicOptions">Anthropic base-URL options (shared with <see cref="AnthropicProvider"/>). The API key field is no longer read — credentials come from <see cref="ILlmCredentialResolver"/>.</param>
    /// <param name="ollamaOptions">Provides the Ollama base URL (shared with <see cref="OllamaProvider"/>).</param>
    /// <param name="scopeFactory">Service-scope factory used to resolve the scoped <see cref="ILlmCredentialResolver"/> per fetch (#615). The resolver depends on the scoped <see cref="Cvoya.Spring.Core.Secrets.ISecretResolver"/> which in turn wraps the scoped <see cref="Cvoya.Spring.Dapr.Secrets.EfSecretRegistry"/> — so the catalog (registered as a singleton for its TTL cache) opens a short-lived scope on each dynamic fetch rather than capturing one.</param>
    /// <param name="timeProvider">Clock abstraction for cache expiry — injected so tests can advance time.</param>
    /// <param name="logger">Logger for fall-back warnings.</param>
    public ModelCatalog(
        IHttpClientFactory httpClientFactory,
        IOptions<AiProviderOptions> anthropicOptions,
        IOptions<OllamaOptions> ollamaOptions,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<ModelCatalog> logger)
        : this(
            httpClientFactory,
            anthropicOptions,
            ollamaOptions,
            ResolveCredentialUsingScopeFactory(scopeFactory, "claude"),
            ResolveCredentialUsingScopeFactory(scopeFactory, "openai"),
            timeProvider,
            logger)
    {
    }

    /// <summary>
    /// Test / advanced-composition constructor. Accepts direct delegates
    /// for credential resolution so tests can stub the tier-2 chain
    /// without spinning up a DI container.
    /// </summary>
    internal ModelCatalog(
        IHttpClientFactory httpClientFactory,
        IOptions<AiProviderOptions> anthropicOptions,
        IOptions<OllamaOptions> ollamaOptions,
        Func<CancellationToken, Task<LlmCredentialResolution>> resolveAnthropicCredential,
        Func<CancellationToken, Task<LlmCredentialResolution>> resolveOpenAiCredential,
        TimeProvider timeProvider,
        ILogger<ModelCatalog> logger)
    {
        _httpClientFactory = httpClientFactory;
        _anthropicOptions = anthropicOptions;
        _ollamaOptions = ollamaOptions;
        _resolveAnthropicCredential = resolveAnthropicCredential;
        _resolveOpenAiCredential = resolveOpenAiCredential;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private static Func<CancellationToken, Task<LlmCredentialResolution>> ResolveCredentialUsingScopeFactory(
        IServiceScopeFactory scopeFactory, string providerId)
    {
        return async ct =>
        {
            // Open a short-lived scope so the scoped ISecretResolver
            // (which wraps the scoped EfSecretRegistry → SpringDbContext)
            // is disposed immediately after the credential read. The
            // wizard-facing endpoint itself runs in a request scope, but
            // we cannot assume the ambient scope here because ModelCatalog
            // is a singleton — relying on DI's RootProvider to resolve a
            // scoped service would throw at runtime.
            await using var scope = scopeFactory.CreateAsyncScope();
            var resolver = scope.ServiceProvider.GetRequiredService<ILlmCredentialResolver>();
            return await resolver.ResolveAsync(providerId, unitName: null, ct).ConfigureAwait(false);
        };
    }

    /// <summary>The HTTP client name used for provider model-catalog fetches.</summary>
    public const string HttpClientName = "ModelCatalogDiscovery";

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return Array.Empty<string>();
        }

        var key = providerId.Trim().ToLowerInvariant();
        var now = _timeProvider.GetUtcNow();

        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
        {
            return entry.Models;
        }

        var models = await LoadAsync(key, cancellationToken).ConfigureAwait(false);
        _cache[key] = new CacheEntry(models, now + CacheTtl);
        return models;
    }

    private async Task<IReadOnlyList<string>> LoadAsync(string providerId, CancellationToken cancellationToken)
    {
        var fallback = StaticFallback.TryGetValue(providerId, out var f) ? f : Array.Empty<string>();

        try
        {
            return providerId switch
            {
                "claude" => await FetchAnthropicAsync(fallback, cancellationToken).ConfigureAwait(false),
                "openai" => await FetchOpenAiAsync(fallback, cancellationToken).ConfigureAwait(false),
                "ollama" => await FetchOllamaAsync(fallback, cancellationToken).ConfigureAwait(false),
                _ => fallback,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dynamic model discovery failed for provider {Provider}; falling back to static list.",
                providerId);
            return fallback;
        }
    }

    private async Task<IReadOnlyList<string>> FetchAnthropicAsync(
        IReadOnlyList<string> fallback,
        CancellationToken cancellationToken)
    {
        // #615: credentials resolve through the tier-2 chain (unit secret
        // → tenant default). The wizard has no "unit context" when it
        // calls this endpoint, so the delegate passes null and relies on
        // the tenant-scoped default. Unit-specific overrides are consumed
        // by the agent runtime, not the wizard.
        var credential = await _resolveAnthropicCredential(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(credential.Value))
        {
            _logger.LogInformation(
                "No Anthropic credential resolved (tenant default '{SecretName}' not set); " +
                "using static model list for the wizard. Set a tenant default via " +
                "`spring secret --scope tenant create {SecretName} --value <...>` or the Tenant defaults panel in the portal.",
                credential.SecretName, credential.SecretName);
            return fallback;
        }

        var apiKey = credential.Value;
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var baseUrl = string.IsNullOrWhiteSpace(_anthropicOptions.Value.BaseUrl)
            ? AnthropicBaseUrl
            : _anthropicOptions.Value.BaseUrl.TrimEnd('/');

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "Anthropic models endpoint returned {StatusCode}; falling back to static list.",
                response.StatusCode);
            return fallback;
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync(ModelCatalogJsonContext.Default.AnthropicModelsResponse, cancellationToken)
            .ConfigureAwait(false);

        var ids = body?.Data?
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

        return ids is { Count: > 0 } ? ids : fallback;
    }

    private async Task<IReadOnlyList<string>> FetchOpenAiAsync(
        IReadOnlyList<string> fallback,
        CancellationToken cancellationToken)
    {
        // #615: resolve through the tier-2 chain. The wizard runs
        // without unit context, so the delegate checks the tenant
        // default. The private cloud host plugs its own tenant-scoped
        // implementation into ILlmCredentialResolver.
        var credential = await _resolveOpenAiCredential(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(credential.Value))
        {
            _logger.LogInformation(
                "No OpenAI credential resolved (tenant default '{SecretName}' not set); " +
                "using static model list for the wizard. Set a tenant default via " +
                "`spring secret --scope tenant create {SecretName} --value <...>` or the Tenant defaults panel in the portal.",
                credential.SecretName, credential.SecretName);
            return fallback;
        }

        var apiKey = credential.Value;

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{OpenAiBaseUrl}/v1/models");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "OpenAI models endpoint returned {StatusCode}; falling back to static list.",
                response.StatusCode);
            return fallback;
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync(ModelCatalogJsonContext.Default.OpenAiModelsResponse, cancellationToken)
            .ConfigureAwait(false);

        var ids = body?.Data?
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            // OpenAI returns 80+ entries including audio, embeddings, etc. Keep
            // the dropdown focused on chat-completions-capable families so the
            // wizard doesn't overwhelm the user. The set is intentionally
            // permissive — any `gpt*`, `o1*`, `o3*`, `o4*` prefix.
            .Where(IsChatModel)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return ids is { Count: > 0 } ? ids : fallback;
    }

    private static bool IsChatModel(string id)
    {
        return id.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("chatgpt", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<string>> FetchOllamaAsync(
        IReadOnlyList<string> fallback,
        CancellationToken cancellationToken)
    {
        // Ollama needs no auth. The dedicated /api/v1/ollama/models endpoint
        // (#350) covers the portal's primary use-case, but routing Ollama
        // through the same seam keeps all providers uniform and lets the
        // wizard query one endpoint regardless of provider choice.
        var baseUrl = (_ollamaOptions.Value.BaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return fallback;
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _ollamaOptions.Value.HealthCheckTimeoutSeconds));

        using var response = await client.GetAsync(new Uri(new Uri(baseUrl), "/api/tags"), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync(ModelCatalogJsonContext.Default.OllamaTagsDto, cancellationToken)
            .ConfigureAwait(false);

        var names = body?.Models?
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        return names is { Count: > 0 } ? names : fallback;
    }

    private readonly record struct CacheEntry(IReadOnlyList<string> Models, DateTimeOffset ExpiresAt);
}

/// <summary>Shape of the Anthropic <c>GET /v1/models</c> response body.</summary>
internal sealed record AnthropicModelsResponse(
    [property: JsonPropertyName("data")] AnthropicModelDto[]? Data);

/// <summary>One entry in the Anthropic models response.</summary>
internal sealed record AnthropicModelDto(
    [property: JsonPropertyName("id")] string? Id);

/// <summary>Shape of the OpenAI <c>GET /v1/models</c> response body.</summary>
internal sealed record OpenAiModelsResponse(
    [property: JsonPropertyName("data")] OpenAiModelDto[]? Data);

/// <summary>One entry in the OpenAI models response.</summary>
internal sealed record OpenAiModelDto(
    [property: JsonPropertyName("id")] string? Id);

/// <summary>Shape of the Ollama <c>GET /api/tags</c> response body.</summary>
internal sealed record OllamaTagsDto(
    [property: JsonPropertyName("models")] OllamaTagDto[]? Models);

/// <summary>One entry in the Ollama tags response.</summary>
internal sealed record OllamaTagDto(
    [property: JsonPropertyName("name")] string? Name);

[JsonSerializable(typeof(AnthropicModelsResponse))]
[JsonSerializable(typeof(OpenAiModelsResponse))]
[JsonSerializable(typeof(OllamaTagsDto))]
internal partial class ModelCatalogJsonContext : JsonSerializerContext;