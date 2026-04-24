// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="ILlmDispatcher"/> implementation that forwards every request
/// through <c>spring-dispatcher</c> so the upstream LLM call executes from
/// a process that can reach the tenant network the worker cannot. The
/// hosted-agent LLM analogue of the dispatcher-proxied A2A primitive
/// shipped for issue #1160 — same idea, different payload. Closes #1168.
/// </summary>
/// <remarks>
/// <para>
/// Wire shape: the worker base64-encodes the request body and posts a
/// JSON envelope to <c>POST /v1/llm/forward</c> on the dispatcher. The
/// dispatcher dispatches the actual upstream request from its own
/// process and returns a base64-encoded body plus the upstream status
/// code. Headers ride on the envelope verbatim. Streaming uses
/// <c>POST /v1/llm/forward/stream</c> and yields the upstream response
/// body bytes as the dispatcher relays them — SSE framing is left to
/// the caller (<see cref="OllamaProvider"/> / <see cref="AnthropicProvider"/>)
/// to parse, exactly as in the direct path.
/// </para>
/// <para>
/// Failure semantics mirror <c>SendHttpJsonAsync</c> on
/// <see cref="IContainerRuntime"/>: any transport failure (the dispatcher
/// itself unreachable, the upstream call refused, a 4xx/5xx from the
/// dispatcher proxy itself) collapses to <c>StatusCode = 502</c> with an
/// empty body for the non-streaming path so the calling provider's retry
/// / failover policy owns the next move uniformly. The streaming path
/// throws on transport failure because partial-stream recovery is the
/// caller's job and an empty enumeration would be silently misread as
/// "the upstream said nothing".
/// </para>
/// </remarks>
public class DispatcherProxiedLlmDispatcher(
    IHttpClientFactory httpClientFactory,
    IOptions<DispatcherClientOptions> options,
    ILoggerFactory loggerFactory) : ILlmDispatcher
{
    private readonly DispatcherClientOptions _options = options.Value;
    private readonly ILogger _logger = loggerFactory.CreateLogger<DispatcherProxiedLlmDispatcher>();

    /// <summary>Name of the named <see cref="HttpClient"/> registered for the LLM proxy path.</summary>
    public const string HttpClientName = "spring-dispatcher-llm";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task<LlmDispatchResponse> SendAsync(
        LlmDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var httpClient = CreateClient();
        var envelope = BuildEnvelope(request);

        _logger.LogDebug("Forwarding LLM request via dispatcher proxy to {Url}", request.Url);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "v1/llm/forward", envelope, JsonOptions, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Dispatcher LLM proxy returned {StatusCode} for {Url}; collapsing to 502.",
                    response.StatusCode, request.Url);
                return new LlmDispatchResponse(StatusCode: 502, Body: []);
            }

            var parsed = await response.Content.ReadFromJsonAsync<LlmForwardResponseEnvelope>(
                JsonOptions, cancellationToken);

            if (parsed is null)
            {
                _logger.LogDebug(
                    "Dispatcher LLM proxy returned an empty body for {Url}; collapsing to 502.",
                    request.Url);
                return new LlmDispatchResponse(StatusCode: 502, Body: []);
            }

            var body = string.IsNullOrEmpty(parsed.BodyBase64)
                ? []
                : Convert.FromBase64String(parsed.BodyBase64);
            return new LlmDispatchResponse(parsed.StatusCode, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled — propagate so the upstream IAiProvider's own
            // OperationCanceledException handling (which most providers
            // explicitly rethrow) sees the cancel cleanly.
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(
                ex,
                "Dispatcher LLM proxy transport failure for {Url}; collapsing to 502.",
                request.Url);
            return new LlmDispatchResponse(StatusCode: 502, Body: []);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SendStreamingAsync(
        LlmDispatchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var httpClient = CreateClient();
        var envelope = BuildEnvelope(request);

        _logger.LogDebug("Forwarding streaming LLM request via dispatcher proxy to {Url}", request.Url);

        var post = new HttpRequestMessage(HttpMethod.Post, "v1/llm/forward/stream")
        {
            Content = JsonContent.Create(envelope, options: JsonOptions),
        };

        // ResponseHeadersRead so we begin reading SSE chunks as soon as the
        // dispatcher relays the first byte; without it HttpClient buffers the
        // full response and we lose the streaming property.
        using var response = await httpClient.SendAsync(
            post, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // We deliberately throw here — the streaming caller has no
            // sentinel for "the upstream gave us nothing" and an empty
            // enumeration would be misread as a clean stream that simply
            // produced no tokens. HttpRequestException matches the direct
            // path's failure shape so existing provider error handlers
            // (which catch HttpRequestException for connection errors) work.
            throw new HttpRequestException(
                $"Dispatcher LLM proxy stream returned {(int)response.StatusCode} for {request.Url}.",
                inner: null,
                statusCode: response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var chunk = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunk, 0, read);
            yield return chunk;
        }
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient(HttpClientName);

        if (client.BaseAddress is null)
        {
            // BaseUrl shape is validated at startup by
            // DispatcherConfigurationRequirement (#639). We keep a defensive
            // throw here so hosts that bypass the validator don't silently
            // fall through to HttpClient with a null BaseAddress.
            if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            {
                throw new InvalidOperationException(
                    "Dispatcher:BaseUrl is not configured. Set it to the spring-dispatcher HTTP endpoint "
                    + "(e.g. http://host.containers.internal:8090/) before enabling the LLM dispatcher proxy.");
            }
            client.BaseAddress = new Uri(_options.BaseUrl.EndsWith('/') ? _options.BaseUrl : _options.BaseUrl + "/");
        }

        if (!string.IsNullOrWhiteSpace(_options.BearerToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.BearerToken);
        }

        return client;
    }

    private static LlmForwardRequestEnvelope BuildEnvelope(LlmDispatchRequest request)
    {
        return new LlmForwardRequestEnvelope
        {
            Url = request.Url,
            BodyBase64 = request.Body.Length == 0 ? string.Empty : Convert.ToBase64String(request.Body),
            Headers = request.Headers is null
                ? null
                : new Dictionary<string, string>(request.Headers),
        };
    }

    /// <summary>
    /// Wire shape sent to <c>POST /v1/llm/forward</c> on the dispatcher.
    /// Duplicated here rather than taking a build dependency on
    /// <c>Cvoya.Spring.Dispatcher</c> so the worker and dispatcher stay
    /// independently deployable (same posture as the existing
    /// <c>DispatcherSendA2ARequest</c>).
    /// </summary>
    internal record LlmForwardRequestEnvelope
    {
        public required string Url { get; init; }
        public required string BodyBase64 { get; init; }
        public IDictionary<string, string>? Headers { get; init; }
    }

    /// <summary>
    /// Wire shape returned by <c>POST /v1/llm/forward</c> on the dispatcher.
    /// </summary>
    internal record LlmForwardResponseEnvelope
    {
        public required int StatusCode { get; init; }
        public required string BodyBase64 { get; init; }
    }
}