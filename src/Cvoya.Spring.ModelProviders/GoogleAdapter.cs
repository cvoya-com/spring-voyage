// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.ModelProviders;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Catalog;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// <see cref="IModelProviderAdapter"/> for the Google Generative Language
/// API (ADR-0038 decision 3). The API authenticates via a query-string
/// <c>?key=…</c> parameter and reports models under
/// <c>{ models: [{ name }] }</c>.
/// </summary>
public sealed class GoogleAdapter : IModelProviderAdapter
{
    /// <summary>Strategy id matching the catalogue entry's <c>adapter</c> field.</summary>
    public const string Id = "google";

    /// <summary>Named <see cref="HttpClient"/> the adapter resolves for live-fetch calls.</summary>
    public const string HttpClientName = "Cvoya.Spring.ModelProviders.Google";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleAdapter> _logger;

    public GoogleAdapter(IHttpClientFactory httpClientFactory, ILogger<GoogleAdapter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? NullLogger<GoogleAdapter>.Instance;
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

        if (string.IsNullOrWhiteSpace(credential))
        {
            return FetchLiveModelsResult.InvalidCredential(
                "Supply a Google API key to fetch the live model catalog.");
        }

        var baseUrl = provider.ApiBaseUrl.TrimEnd('/');
        var endpoint = provider.ModelsEndpoint.StartsWith('/') ? provider.ModelsEndpoint : "/" + provider.ModelsEndpoint;
        var url = $"{baseUrl}{endpoint}?key={Uri.EscapeDataString(credential)}";

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                return FetchLiveModelsResult.InvalidCredential(
                    $"Google rejected the key (HTTP {(int)response.StatusCode}).");
            }
            if (!response.IsSuccessStatusCode)
            {
                return FetchLiveModelsResult.NetworkError(
                    $"Google responded with HTTP {(int)response.StatusCode} {response.StatusCode}.");
            }

            var payload = await response.Content
                .ReadFromJsonAsync(GoogleJson.Default.GoogleModelsResponse, cancellationToken)
                .ConfigureAwait(false);

            return FetchLiveModelsResult.Success(BuildModels(payload));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network error fetching Google live model list.");
            return FetchLiveModelsResult.NetworkError($"Could not reach the Google API: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Timeout fetching Google live model list.");
            return FetchLiveModelsResult.NetworkError("Timed out contacting the Google API.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Google live-models response.");
            return FetchLiveModelsResult.NetworkError("The Google API returned an unexpected response body.");
        }
    }

    /// <inheritdoc />
    public bool IsCredentialFormatAccepted(ModelProvider provider, string credential, AuthMethod authMethod)
    {
        // Google API keys are typically 39 chars with no fixed prefix; the
        // wizard's only signal is non-empty.
        return !string.IsNullOrWhiteSpace(credential);
    }

    private static IReadOnlyList<ModelDescriptor> BuildModels(GoogleModelsResponse? payload)
    {
        if (payload?.Models is null || payload.Models.Length == 0)
        {
            return Array.Empty<ModelDescriptor>();
        }

        var result = new List<ModelDescriptor>(payload.Models.Length);
        foreach (var entry in payload.Models)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }
            // Google's `name` is fully qualified (e.g. `models/gemini-1.5-pro`);
            // strip the prefix so the id matches the catalogue's
            // unqualified shape.
            var id = entry.Name!.StartsWith("models/", StringComparison.Ordinal)
                ? entry.Name[7..]
                : entry.Name;
            var display = string.IsNullOrWhiteSpace(entry.DisplayName) ? id : entry.DisplayName!;
            result.Add(new ModelDescriptor(id, display, ContextWindow: null));
        }
        return result;
    }
}

internal sealed record GoogleModelsResponse(
    [property: JsonPropertyName("models")] GoogleModelEntry[]? Models);

internal sealed record GoogleModelEntry(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("displayName")] string? DisplayName);

[JsonSerializable(typeof(GoogleModelsResponse))]
internal partial class GoogleJson : JsonSerializerContext;