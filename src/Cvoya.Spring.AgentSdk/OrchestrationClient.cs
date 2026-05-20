// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// HTTP implementation of <see cref="IOrchestrationClient"/>.
/// </summary>
public sealed class OrchestrationClient : IOrchestrationClient
{
    private const string ResultEndpoint = "result";
    private const string DelegateEndpoint = "delegate-to";
    private const string FanoutEndpoint = "fanout-to";
    private const string AgentAddressClaim = "sv_addr";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string _callbackToken;
    private readonly string _callerAddress;

    public OrchestrationClient(string baseUrl, string callbackToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackToken);

        _httpClient = new HttpClient
        {
            BaseAddress = BuildOrchestrationBaseUri(baseUrl),
        };
        _callbackToken = callbackToken;
        _callerAddress = CallbackTokenReader.TryReadStringClaim(callbackToken, AgentAddressClaim) ?? string.Empty;
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

        var response = await SendAsync<DelegateToRequest, DelegateToResponse>(
            DelegateEndpoint,
            new DelegateToRequest(
                _callerAddress,
                BuildTargetAddress(targetUnitId),
                ParseThreadId(threadId, nameof(threadId)),
                MessageId: Guid.Empty,
                prompt,
                Reason: null),
            cancellationToken).ConfigureAwait(false);

        return new DelegateResponse(
            response.Message?.MessageId.ToString("D") ?? string.Empty,
            response.Message?.MessageContent ?? string.Empty);
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

        var response = await SendAsync<FanoutToRequest, FanoutToResponse>(
            FanoutEndpoint,
            new FanoutToRequest(
                _callerAddress,
                targetUnitIds.Select(BuildTargetAddress).ToArray(),
                ParseThreadId(threadId, nameof(threadId)),
                MessageId: Guid.Empty,
                prompt,
                Reason: null),
            cancellationToken).ConfigureAwait(false);

        return new FanoutResponse(
            response.Results
                .Select(result => new FanoutResult(
                    result.Target,
                    result.Message?.MessageId.ToString("D"),
                    result.Message?.MessageContent,
                    result.ErrorMessage))
                .ToArray());
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
        var relativePrefix = AgentSdkEnvironmentContract.OrchestrationRoutePrefix.TrimStart('/');

        return new Uri(normalizedBase, relativePrefix + "/");
    }

    private static Guid ParseThreadId(string threadId, string parameterName)
    {
        if (!Guid.TryParse(threadId, out var parsed))
        {
            throw new ArgumentException(
                "Thread id must be a valid Guid string.",
                parameterName);
        }

        return parsed;
    }

    private static string BuildTargetAddress(string targetUnitId)
    {
        if (targetUnitId.Contains(':', StringComparison.Ordinal))
        {
            return targetUnitId;
        }

        return Guid.TryParse(targetUnitId, out var targetId)
            ? $"unit:{targetId:N}"
            : targetUnitId;
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
        var reason = MapDispatcherErrorReason(error?.Error);
        var message = string.IsNullOrWhiteSpace(error?.Message)
            ? $"Spring Voyage orchestration dispatcher returned HTTP {(int)response.StatusCode}."
            : error.Message;

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
            IsAuthorizationRejection(response.StatusCode, reason))
        {
            throw new OrchestrationAuthException(message, reason);
        }

        throw new OrchestrationTransportException(
            $"Spring Voyage orchestration dispatcher returned HTTP {(int)response.StatusCode}: {message}");
    }

    private static bool IsAuthorizationRejection(HttpStatusCode statusCode, string? reason) =>
        reason is not null &&
        (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound ||
         (int)statusCode == StatusCodes.TooManyRequests);

    private static string? MapDispatcherErrorReason(string? errorCode) =>
        errorCode switch
        {
            null or "" => null,
            "InvalidToken" => "InvalidToken",
            "OrchestrationSelfDelegation" => "SelfDelegation",
            "OrchestrationDepthExceeded" => "DepthExceeded",
            "OrchestrationCrossTenant" => "CrossTenant",
            _ => errorCode,
        };

    private static async Task<DispatcherError?> ReadErrorBodyAsync(
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
                return JsonSerializer.Deserialize<DispatcherError>(body, JsonOptions);
            }
            catch (JsonException)
            {
                return new DispatcherError(null, body);
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

    private sealed record DelegateToRequest(
        [property: JsonPropertyName("callerAddress")] string CallerAddress,
        [property: JsonPropertyName("targetAddress")] string TargetAddress,
        [property: JsonPropertyName("threadId")] Guid ThreadId,
        [property: JsonPropertyName("messageId")] Guid MessageId,
        [property: JsonPropertyName("messageContent")] string MessageContent,
        [property: JsonPropertyName("reason")] string? Reason);

    private sealed record DelegateToResponse(
        [property: JsonPropertyName("message")] OrchestrationCallbackMessage? Message);

    private sealed record FanoutToRequest(
        [property: JsonPropertyName("callerAddress")] string CallerAddress,
        [property: JsonPropertyName("targetAddresses")] string[] TargetAddresses,
        [property: JsonPropertyName("threadId")] Guid ThreadId,
        [property: JsonPropertyName("messageId")] Guid MessageId,
        [property: JsonPropertyName("messageContent")] string MessageContent,
        [property: JsonPropertyName("reason")] string? Reason);

    private sealed record FanoutToResponse(
        [property: JsonPropertyName("results")] FanoutTargetResult[] Results);

    private sealed record FanoutTargetResult(
        [property: JsonPropertyName("target")] string Target,
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
        [property: JsonPropertyName("message")] OrchestrationCallbackMessage? Message);

    private sealed record OrchestrationCallbackMessage(
        [property: JsonPropertyName("messageId")] Guid MessageId,
        [property: JsonPropertyName("fromAddress")] string FromAddress,
        [property: JsonPropertyName("toAddress")] string ToAddress,
        [property: JsonPropertyName("threadId")] string? ThreadId,
        [property: JsonPropertyName("messageContent")] string MessageContent);

    private sealed record DispatcherError(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("message")] string? Message);

    private static class StatusCodes
    {
        public const int TooManyRequests = 429;
    }

    private static class CallbackTokenReader
    {
        public static string? TryReadStringClaim(string token, string claimName)
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            try
            {
                var payload = DecodeBase64Url(parts[1]);
                using var document = JsonDocument.Parse(payload);
                return document.RootElement.TryGetProperty(claimName, out var claim) &&
                    claim.ValueKind == JsonValueKind.String
                        ? claim.GetString()
                        : null;
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return null;
            }
        }

        private static byte[] DecodeBase64Url(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = (padded.Length % 4) switch
            {
                0 => padded,
                2 => padded + "==",
                3 => padded + "=",
                _ => throw new FormatException("Invalid base64url payload length."),
            };

            return Convert.FromBase64String(padded);
        }
    }
}
