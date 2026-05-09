// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// HTTP implementation of <see cref="IOrchestrationClient"/>.
/// </summary>
public sealed class OrchestrationClient : IOrchestrationClient
{
    private const string ResultEndpoint = "result";
    private const string DelegateEndpoint = "delegate";
    private const string FanoutEndpoint = "fanout";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string _callbackToken;

    public OrchestrationClient(string baseUrl, string callbackToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackToken);

        _httpClient = new HttpClient
        {
            BaseAddress = BuildOrchestrationBaseUri(baseUrl),
        };
        _callbackToken = callbackToken;
    }

    /// <inheritdoc />
    public async Task PostResultAsync(
        string threadId,
        string result,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(result);

        await SendAsync(
            ResultEndpoint,
            new PostResultRequest(threadId, result),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DelegateResponse> DelegateAsync(
        string threadId,
        string targetUnitId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUnitId);
        ArgumentNullException.ThrowIfNull(prompt);

        return await SendAsync<DelegateRequest, DelegateResponse>(
            DelegateEndpoint,
            new DelegateRequest(threadId, targetUnitId, prompt),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FanoutResponse> FanoutAsync(
        string threadId,
        IReadOnlyList<string> targetUnitIds,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(targetUnitIds);
        ArgumentNullException.ThrowIfNull(prompt);

        return await SendAsync<FanoutRequest, FanoutResponse>(
            FanoutEndpoint,
            new FanoutRequest(threadId, targetUnitIds, prompt),
            cancellationToken).ConfigureAwait(false);
    }

    private static Uri BuildOrchestrationBaseUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Callback base URL must be an absolute http(s) URL.",
                nameof(baseUrl));
        }

        var normalizedBase = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/");
        var relativePrefix = AgentCallbackEnvironmentContract.OrchestrationRoutePrefix.TrimStart('/');

        return new Uri(normalizedBase, relativePrefix + "/");
    }

    private async Task SendAsync<TRequest>(
        string endpoint,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(endpoint, request, cancellationToken)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string endpoint,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(endpoint, request, cancellationToken)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await DeserializeResponseAsync<TResponse>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendRequestAsync<TRequest>(
        string endpoint,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _callbackToken);

        try
        {
            return await _httpClient.SendAsync(httpRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OrchestrationTransportException(
                "Failed to call the Spring Voyage orchestration dispatcher.",
                ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OrchestrationTransportException(
                "Timed out calling the Spring Voyage orchestration dispatcher.",
                ex);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(error)
            ? $"Spring Voyage orchestration dispatcher returned HTTP {(int)response.StatusCode}."
            : error;

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new OrchestrationAuthException(message);
        }

        throw new OrchestrationTransportException(
            $"Spring Voyage orchestration dispatcher returned HTTP {(int)response.StatusCode}: {message}");
    }

    private static async Task<string?> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                var error = JsonSerializer.Deserialize<DispatcherError>(body, JsonOptions);
                return error?.Message ?? error?.Error ?? body;
            }
            catch (JsonException)
            {
                return body;
            }
        }
        catch (HttpRequestException ex)
        {
            throw new OrchestrationTransportException(
                "Failed to read the Spring Voyage orchestration dispatcher error response.",
                ex);
        }
    }

    private static async Task<TResponse> DeserializeResponseAsync<TResponse>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var value = await JsonSerializer.DeserializeAsync<TResponse>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return value
                ?? throw new OrchestrationTransportException(
                    "Spring Voyage orchestration dispatcher returned an empty response body.");
        }
        catch (JsonException ex)
        {
            throw new OrchestrationTransportException(
                "Spring Voyage orchestration dispatcher returned an invalid JSON response body.",
                ex);
        }
    }

    private sealed record PostResultRequest(
        [property: JsonPropertyName("threadId")] string ThreadId,
        [property: JsonPropertyName("result")] string Result);

    private sealed record DelegateRequest(
        [property: JsonPropertyName("threadId")] string ThreadId,
        [property: JsonPropertyName("targetUnitId")] string TargetUnitId,
        [property: JsonPropertyName("prompt")] string Prompt);

    private sealed record FanoutRequest(
        [property: JsonPropertyName("threadId")] string ThreadId,
        [property: JsonPropertyName("targetUnitIds")] IReadOnlyList<string> TargetUnitIds,
        [property: JsonPropertyName("prompt")] string Prompt);

    private sealed record DispatcherError(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("message")] string? Message);
}