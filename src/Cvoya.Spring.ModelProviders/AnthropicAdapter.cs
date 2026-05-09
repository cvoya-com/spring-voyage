// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.ModelProviders;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.ModelProviders;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// <see cref="IModelProviderAdapter"/> for the Anthropic Messages API
/// (ADR-0038 decision 3). Handles the bespoke header shape
/// (<c>x-api-key</c>, <c>anthropic-version</c>) and the OAuth-vs-api-key
/// format split that Anthropic's API enforces.
/// </summary>
public sealed class AnthropicAdapter : IModelProviderAdapter
{
    /// <summary>Strategy id matching the catalogue entry's <c>adapter</c> field.</summary>
    public const string Id = "anthropic";

    /// <summary>Named <see cref="HttpClient"/> the adapter resolves for live-fetch calls.</summary>
    public const string HttpClientName = "Cvoya.Spring.ModelProviders.Anthropic";

    private const string AnthropicVersion = "2023-06-01";
    private const string OAuthTokenPrefix = "sk-ant-oat";
    private const string ApiKeyPrefix = "sk-ant-api";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnthropicAdapter> _logger;

    public AnthropicAdapter(IHttpClientFactory httpClientFactory, ILogger<AnthropicAdapter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? NullLogger<AnthropicAdapter>.Instance;
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
                "Supply an Anthropic API key (sk-ant-api…) to fetch the live model catalog.");
        }

        // Anthropic's REST endpoint rejects Claude.ai OAuth tokens with a
        // 401 indistinguishable from a bad key; surface this precisely
        // rather than misreporting "invalid".
        if (credential.StartsWith(OAuthTokenPrefix, StringComparison.Ordinal))
        {
            return FetchLiveModelsResult.Unsupported(
                "Claude.ai OAuth tokens cannot enumerate models through the Anthropic Platform REST API. " +
                "Supply an Anthropic API key (sk-ant-api…) to refresh, or keep the seed catalog.");
        }

        var baseUrl = provider.ApiBaseUrl.TrimEnd('/');
        var endpoint = provider.ModelsEndpoint.StartsWith('/') ? provider.ModelsEndpoint : "/" + provider.ModelsEndpoint;

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + endpoint);
        request.Headers.Add("x-api-key", credential);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return FetchLiveModelsResult.InvalidCredential(
                    $"Anthropic rejected the key (HTTP {(int)response.StatusCode}).");
            }
            if (!response.IsSuccessStatusCode)
            {
                return FetchLiveModelsResult.NetworkError(
                    $"Anthropic responded with HTTP {(int)response.StatusCode} {response.StatusCode}.");
            }

            var payload = await response.Content
                .ReadFromJsonAsync(AnthropicJson.Default.AnthropicModelsResponse, cancellationToken)
                .ConfigureAwait(false);

            return FetchLiveModelsResult.Success(BuildModels(payload));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network error fetching Anthropic live model list.");
            return FetchLiveModelsResult.NetworkError($"Could not reach the Anthropic API: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Timeout fetching Anthropic live model list.");
            return FetchLiveModelsResult.NetworkError("Timed out contacting the Anthropic API.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Anthropic live-models response.");
            return FetchLiveModelsResult.NetworkError("The Anthropic API returned an unexpected response body.");
        }
    }

    /// <inheritdoc />
    public bool IsCredentialFormatAccepted(ModelProvider provider, string credential, AuthMethod authMethod)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return true;
        }

        var isApiKey = credential.StartsWith(ApiKeyPrefix, StringComparison.Ordinal);
        var isOAuth = credential.StartsWith(OAuthTokenPrefix, StringComparison.Ordinal);

        if (!isApiKey && !isOAuth)
        {
            return false;
        }

        return authMethod switch
        {
            AuthMethod.Oauth => isOAuth,
            AuthMethod.ApiKey => isApiKey,
            _ => false,
        };
    }

    private static IReadOnlyList<ModelDescriptor> BuildModels(AnthropicModelsResponse? payload)
    {
        if (payload?.Data is null || payload.Data.Length == 0)
        {
            return Array.Empty<ModelDescriptor>();
        }

        var result = new List<ModelDescriptor>(payload.Data.Length);
        foreach (var entry in payload.Data)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                continue;
            }
            var display = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Id! : entry.DisplayName!;
            result.Add(new ModelDescriptor(entry.Id!, display, ContextWindow: null));
        }
        return result;
    }
}

internal sealed record AnthropicModelsResponse(
    [property: JsonPropertyName("data")] AnthropicModelDto[]? Data);

internal sealed record AnthropicModelDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("display_name")] string? DisplayName);

[JsonSerializable(typeof(AnthropicModelsResponse))]
internal partial class AnthropicJson : JsonSerializerContext;
