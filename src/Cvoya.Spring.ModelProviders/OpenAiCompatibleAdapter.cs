// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.ModelProviders;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.ModelProviders;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// <see cref="IModelProviderAdapter"/> for any provider speaking the
/// OpenAI Chat Completions wire format. Covers OpenAI itself and Ollama
/// (which exposes an OpenAI-compatible surface) per ADR-0038 decision 3.
/// </summary>
/// <remarks>
/// <para>
/// Live-model fetch supports two response shapes: the canonical OpenAI
/// <c>{ data: [{ id }] }</c> (OpenAI, Ollama via <c>/v1/models</c>) and
/// the Ollama-native <c>{ models: [{ name }] }</c> (returned by
/// <c>/api/tags</c>). The adapter probes both keys so a catalogue entry
/// can target either endpoint without per-provider branching.
/// </para>
/// <para>
/// Credential format rules are intentionally permissive — OpenAI accepts
/// numerous prefixes (<c>sk-…</c>, <c>sk-proj-…</c>, organisation-scoped
/// variants), so the format check rejects only empty strings. Ollama's
/// catalogue entry has empty <see cref="ModelProvider.AuthMethods"/>; the
/// resolver skips this adapter for Ollama's credential-less path.
/// </para>
/// </remarks>
public sealed class OpenAiCompatibleAdapter : IModelProviderAdapter
{
    /// <summary>Strategy id matching the catalogue entry's <c>adapter</c> field.</summary>
    public const string Id = "openai-compatible";

    /// <summary>Named <see cref="HttpClient"/> the adapter resolves for live-fetch calls.</summary>
    public const string HttpClientName = "Cvoya.Spring.ModelProviders.OpenAiCompatible";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiCompatibleAdapter> _logger;

    public OpenAiCompatibleAdapter(IHttpClientFactory httpClientFactory, ILogger<OpenAiCompatibleAdapter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? NullLogger<OpenAiCompatibleAdapter>.Instance;
    }

    /// <inheritdoc />
    public string AdapterId => Id;

    /// <inheritdoc />
    public async Task<FetchLiveModelsResult> FetchLiveModelsAsync(
        ModelProvider provider,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var requiresCredential = provider.AuthMethods.Count > 0;
        if (requiresCredential && string.IsNullOrWhiteSpace(credential))
        {
            return FetchLiveModelsResult.InvalidCredential(
                $"Supply an API key for {provider.DisplayName} to fetch the live model catalog.");
        }

        var baseUrl = provider.ApiBaseUrl.TrimEnd('/');
        var endpoint = provider.ModelsEndpoint.StartsWith('/') ? provider.ModelsEndpoint : "/" + provider.ModelsEndpoint;

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + endpoint);
        if (requiresCredential && !string.IsNullOrWhiteSpace(credential))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return FetchLiveModelsResult.InvalidCredential(
                    $"{provider.DisplayName} rejected the key (HTTP {(int)response.StatusCode}).");
            }
            if (!response.IsSuccessStatusCode)
            {
                return FetchLiveModelsResult.NetworkError(
                    $"{provider.DisplayName} responded with HTTP {(int)response.StatusCode} {response.StatusCode}.");
            }

            var payload = await response.Content
                .ReadFromJsonAsync(OpenAiCompatibleJson.Default.OpenAiCompatibleModelsResponse, cancellationToken)
                .ConfigureAwait(false);

            return FetchLiveModelsResult.Success(BuildModels(payload));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network error fetching {Provider} live model list.", provider.Id);
            return FetchLiveModelsResult.NetworkError($"Could not reach {provider.DisplayName}: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Timeout fetching {Provider} live model list.", provider.Id);
            return FetchLiveModelsResult.NetworkError($"Timed out contacting {provider.DisplayName}.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse {Provider} live-models response.", provider.Id);
            return FetchLiveModelsResult.NetworkError($"{provider.DisplayName} returned an unexpected response body.");
        }
    }

    /// <inheritdoc />
    public bool IsCredentialFormatAccepted(ModelProvider provider, string credential, AuthMethod authMethod)
    {
        // OpenAI-compatible endpoints accept many key shapes; reject only
        // obvious whitespace-only inputs. The empty-string case is the
        // resolver's "not configured" state.
        return !string.IsNullOrWhiteSpace(credential);
    }

    private static IReadOnlyList<ModelDescriptor> BuildModels(OpenAiCompatibleModelsResponse? payload)
    {
        if (payload is null)
        {
            return Array.Empty<ModelDescriptor>();
        }

        var result = new List<ModelDescriptor>();

        // OpenAI shape: { data: [{ id }] } — also returned by Ollama's
        // OpenAI-compatible endpoint at /v1/models.
        if (payload.Data is { Length: > 0 })
        {
            foreach (var entry in payload.Data)
            {
                if (!string.IsNullOrWhiteSpace(entry.Id))
                {
                    result.Add(new ModelDescriptor(entry.Id!, entry.Id!, ContextWindow: null));
                }
            }
        }

        // Ollama-native shape: { models: [{ name }] } at /api/tags.
        if (payload.Models is { Length: > 0 })
        {
            foreach (var entry in payload.Models)
            {
                if (!string.IsNullOrWhiteSpace(entry.Name))
                {
                    result.Add(new ModelDescriptor(entry.Name!, entry.Name!, ContextWindow: null));
                }
            }
        }

        return result;
    }
}

internal sealed record OpenAiCompatibleModelsResponse(
    [property: JsonPropertyName("data")] OpenAiCompatibleModelEntry[]? Data,
    [property: JsonPropertyName("models")] OllamaTagEntry[]? Models);

internal sealed record OpenAiCompatibleModelEntry(
    [property: JsonPropertyName("id")] string? Id);

internal sealed record OllamaTagEntry(
    [property: JsonPropertyName("name")] string? Name);

[JsonSerializable(typeof(OpenAiCompatibleModelsResponse))]
internal partial class OpenAiCompatibleJson : JsonSerializerContext;