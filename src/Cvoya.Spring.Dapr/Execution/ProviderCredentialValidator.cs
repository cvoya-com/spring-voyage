// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="IProviderCredentialValidator"/> implementation
/// (#655). Validates a caller-supplied API key by issuing a read-only
/// <c>GET /v1/models</c> request against the provider, and returns the
/// live model list so the caller can seed a dropdown without a second
/// round-trip.
/// </summary>
/// <remarks>
/// Uses the same HTTP client pool as <see cref="ModelCatalog"/>
/// (<see cref="ModelCatalog.HttpClientName"/>). A dedicated pool is not
/// warranted — the validator is a low-frequency call (operator clicks
/// Validate in the wizard) and the handler lifecycle is identical.
/// </remarks>
public class ProviderCredentialValidator : IProviderCredentialValidator
{
    private const string AnthropicBaseUrl = "https://api.anthropic.com";
    private const string AnthropicVersion = "2023-06-01";
    private const string OpenAiBaseUrl = "https://api.openai.com";
    private const string GoogleBaseUrl = "https://generativelanguage.googleapis.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<AiProviderOptions> _anthropicOptions;
    private readonly IReadOnlyDictionary<string, IProviderCliInvoker> _cliInvokers;
    private readonly ILogger<ProviderCredentialValidator> _logger;

    /// <summary>Creates a new <see cref="ProviderCredentialValidator"/>.</summary>
    public ProviderCredentialValidator(
        IHttpClientFactory httpClientFactory,
        IOptions<AiProviderOptions> anthropicOptions,
        IEnumerable<IProviderCliInvoker> cliInvokers,
        ILogger<ProviderCredentialValidator> logger)
    {
        _httpClientFactory = httpClientFactory;
        _anthropicOptions = anthropicOptions;
        // Last registration wins per provider id — lets the private
        // cloud host replace a default invoker without untangling
        // registration order.
        _cliInvokers = cliInvokers
            .GroupBy(i => i.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ProviderCredentialValidationResult> ValidateAsync(
        string providerId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ProviderCredentialValidationResult(
                ProviderCredentialValidationStatus.MissingKey,
                Models: null,
                ErrorMessage: "Supply an API key to validate.");
        }

        var normalized = (providerId ?? string.Empty).Trim().ToLowerInvariant();

        try
        {
            return normalized switch
            {
                "claude" or "anthropic" => await ValidateAnthropicAsync(apiKey, cancellationToken).ConfigureAwait(false),
                "openai" => await ValidateOpenAiAsync(apiKey, cancellationToken).ConfigureAwait(false),
                "google" or "gemini" or "googleai" => await ValidateGoogleAsync(apiKey, cancellationToken).ConfigureAwait(false),
                _ => new ProviderCredentialValidationResult(
                    ProviderCredentialValidationStatus.UnknownProvider,
                    Models: null,
                    ErrorMessage: $"Unknown provider '{providerId}'. Expected one of: anthropic, openai, google."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Network error validating {Provider} credential.", normalized);
            return new ProviderCredentialValidationResult(
                ProviderCredentialValidationStatus.NetworkError,
                Models: null,
                ErrorMessage: $"Could not reach the {normalized} API: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex,
                "Timeout validating {Provider} credential.", normalized);
            return new ProviderCredentialValidationResult(
                ProviderCredentialValidationStatus.NetworkError,
                Models: null,
                ErrorMessage: $"Timed out contacting the {normalized} API.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse {Provider} validation response.", normalized);
            return new ProviderCredentialValidationResult(
                ProviderCredentialValidationStatus.ProviderError,
                Models: null,
                ErrorMessage: $"The {normalized} API returned an unexpected response body.");
        }
    }

    private async Task<ProviderCredentialValidationResult> ValidateAnthropicAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        // #660: two credential formats reach this path:
        //   * Anthropic API keys (sk-ant-api...) — work with api.anthropic.com.
        //   * Claude.ai OAuth tokens (sk-ant-oat...) — DO NOT work with
        //     api.anthropic.com but DO work when handed to the claude CLI.
        // If a registered CLI invoker reports availability, delegate to
        // it for both formats so the wizard never has to distinguish
        // them. If the CLI is unavailable and the credential is an
        // OAuth token, return a clear error rather than silently hitting
        // REST and surfacing a generic 401.
        var isOAuthToken = apiKey.StartsWith(
            ClaudeCliInvoker.OAuthTokenPrefix, StringComparison.Ordinal);

        if (_cliInvokers.TryGetValue("anthropic", out var cliInvoker))
        {
            var cliAvailable = await cliInvoker.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
            if (cliAvailable)
            {
                var cliResult = await cliInvoker.ValidateAsync(apiKey, cancellationToken).ConfigureAwait(false);
                return MapCliResult(cliResult, providerDisplayName: "Anthropic", providerId: "claude");
            }

            if (isOAuthToken)
            {
                // The REST fallback cannot validate an OAuth token — the
                // Anthropic Platform API rejects it with a 401 that is
                // indistinguishable from a bad key. Tell the operator
                // precisely why validation cannot proceed instead.
                return new ProviderCredentialValidationResult(
                    ProviderCredentialValidationStatus.ProviderError,
                    Models: null,
                    ErrorMessage: "Claude.ai tokens (from `claude setup-token`) require the claude CLI on the host to validate. " +
                        "Install Claude Code on this host, or supply an Anthropic API key (sk-ant-api…) instead.");
            }
        }
        else if (isOAuthToken)
        {
            // No CLI invoker is registered at all — same failure mode as above.
            return new ProviderCredentialValidationResult(
                ProviderCredentialValidationStatus.ProviderError,
                Models: null,
                ErrorMessage: "Claude.ai tokens (from `claude setup-token`) require the claude CLI on the host to validate. " +
                    "Install Claude Code on this host, or supply an Anthropic API key (sk-ant-api…) instead.");
        }

        // REST fallback — for API keys when the CLI is unavailable.
        var baseUrl = string.IsNullOrWhiteSpace(_anthropicOptions.Value.BaseUrl)
            ? AnthropicBaseUrl
            : _anthropicOptions.Value.BaseUrl.TrimEnd('/');

        var client = _httpClientFactory.CreateClient(ModelCatalog.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ProviderCredentialValidationResult(
                ProviderCredentialValidationStatus.Unauthorized,
                Models: null,
                ErrorMessage: "Anthropic rejected the key (HTTP " + (int)response.StatusCode + "). Check that it is a live API key with models access.");
        }
        if (!response.IsSuccessStatusCode)
        {
            return new ProviderCredentialValidationResult(
                ProviderCredentialValidationStatus.ProviderError,
                Models: null,
                ErrorMessage: $"Anthropic responded with HTTP {(int)response.StatusCode} {response.StatusCode}.");
        }

        var body = await response.Content
            .ReadFromJsonAsync(ProviderCredentialValidatorJsonContext.Default.ProviderModelsResponse, cancellationToken)
            .ConfigureAwait(false);

        var ids = ExtractIds(body?.Data);
        return new ProviderCredentialValidationResult(
            ProviderCredentialValidationStatus.Valid,
            Models: ids,
            ErrorMessage: null);
    }

    /// <summary>
    /// Projects a <see cref="ProviderCliValidationResult"/> into the
    /// public <see cref="ProviderCredentialValidationResult"/>. When the
    /// CLI reports success without a model list (today's reality — the
    /// <c>claude</c> CLI has no <c>models</c> subcommand) we backfill
    /// from <see cref="ModelCatalog.StaticFallback"/> so the wizard's
    /// Model dropdown still has something live-ish to render.
    /// </summary>
    private static ProviderCredentialValidationResult MapCliResult(
        ProviderCliValidationResult cli,
        string providerDisplayName,
        string providerId)
    {
        if (cli.Status != ProviderCredentialValidationStatus.Valid)
        {
            return new ProviderCredentialValidationResult(
                cli.Status,
                Models: null,
                ErrorMessage: cli.ErrorMessage);
        }

        IReadOnlyList<string> models = cli.Models
            ?? (ModelCatalog.StaticFallback.TryGetValue(providerId, out var fallback)
                ? fallback
                : Array.Empty<string>());

        _ = providerDisplayName; // reserved for future error contextualisation
        return new ProviderCredentialValidationResult(
            ProviderCredentialValidationStatus.Valid,
            Models: models,
            ErrorMessage: null);
    }

    private async Task<ProviderCredentialValidationResult> ValidateOpenAiAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(ModelCatalog.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{OpenAiBaseUrl}/v1/models");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ProviderCredentialValidationResult(
                ProviderCredentialValidationStatus.Unauthorized,
                Models: null,
                ErrorMessage: "OpenAI rejected the key (HTTP " + (int)response.StatusCode + "). Check that it is a live API key.");
        }
        if (!response.IsSuccessStatusCode)
        {
            return new ProviderCredentialValidationResult(
                ProviderCredentialValidationStatus.ProviderError,
                Models: null,
                ErrorMessage: $"OpenAI responded with HTTP {(int)response.StatusCode} {response.StatusCode}.");
        }

        var body = await response.Content
            .ReadFromJsonAsync(ProviderCredentialValidatorJsonContext.Default.ProviderModelsResponse, cancellationToken)
            .ConfigureAwait(false);

        var ids = ExtractIds(body?.Data)
            .Where(IsChatModel)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return new ProviderCredentialValidationResult(
            ProviderCredentialValidationStatus.Valid,
            Models: ids,
            ErrorMessage: null);
    }

    private async Task<ProviderCredentialValidationResult> ValidateGoogleAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(ModelCatalog.HttpClientName);
        var uri = $"{GoogleBaseUrl}/v1beta/models?key={Uri.EscapeDataString(apiKey)}";

        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.BadRequest)
        {
            return new ProviderCredentialValidationResult(
                ProviderCredentialValidationStatus.Unauthorized,
                Models: null,
                ErrorMessage: "Google rejected the key (HTTP " + (int)response.StatusCode + "). Make sure it is a Google AI Studio API key with Generative Language API access.");
        }
        if (!response.IsSuccessStatusCode)
        {
            return new ProviderCredentialValidationResult(
                ProviderCredentialValidationStatus.ProviderError,
                Models: null,
                ErrorMessage: $"Google responded with HTTP {(int)response.StatusCode} {response.StatusCode}.");
        }

        var body = await response.Content
            .ReadFromJsonAsync(ProviderCredentialValidatorJsonContext.Default.GoogleModelsResponse, cancellationToken)
            .ConfigureAwait(false);

        var ids = body?.Models?
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(StripGoogleModelsPrefix)
            .ToList() ?? new List<string>();

        return new ProviderCredentialValidationResult(
            ProviderCredentialValidationStatus.Valid,
            Models: ids,
            ErrorMessage: null);
    }

    private static string StripGoogleModelsPrefix(string? name)
    {
        // Google returns "models/gemini-..." — the wizard's dropdown
        // carries bare ids everywhere else, so normalize here rather
        // than leaking the "models/" prefix into portal state.
        const string prefix = "models/";
        if (name is null) return string.Empty;
        return name.StartsWith(prefix, StringComparison.Ordinal)
            ? name[prefix.Length..]
            : name;
    }

    private static List<string> ExtractIds(ProviderModelDto[]? data)
    {
        if (data is null || data.Length == 0) return new List<string>();
        return data
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();
    }

    private static bool IsChatModel(string id)
    {
        return id.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("chatgpt", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Shape of Anthropic/OpenAI <c>GET /v1/models</c> response bodies.</summary>
internal sealed record ProviderModelsResponse(
    [property: JsonPropertyName("data")] ProviderModelDto[]? Data);

/// <summary>One entry in the provider models response.</summary>
internal sealed record ProviderModelDto(
    [property: JsonPropertyName("id")] string? Id);

/// <summary>Shape of Google's <c>GET /v1beta/models</c> response body.</summary>
internal sealed record GoogleModelsResponse(
    [property: JsonPropertyName("models")] GoogleModelDto[]? Models);

/// <summary>One entry in the Google models response.</summary>
internal sealed record GoogleModelDto(
    [property: JsonPropertyName("name")] string? Name);

[JsonSerializable(typeof(ProviderModelsResponse))]
[JsonSerializable(typeof(GoogleModelsResponse))]
internal partial class ProviderCredentialValidatorJsonContext : JsonSerializerContext;