// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;

/// <summary>
/// Direct <see cref="ILlmDispatcher"/> implementation that sends every
/// request straight to the upstream URL via an injected
/// <see cref="HttpClient"/>. The OSS default — <c>spring-ollama</c> is
/// dual-attached to <c>spring-net</c> and <c>spring-tenant-default</c>, so
/// a worker on <c>spring-net</c> can still resolve
/// <c>spring-ollama:11434</c> directly. The cloud overlay (and OSS
/// deployments that move Ollama off <c>spring-net</c> entirely) swap this
/// out for <see cref="DispatcherProxiedLlmDispatcher"/>; ADR 0028
/// Decision E.
/// </summary>
/// <remarks>
/// <para>
/// Behaviour preserves what <see cref="OllamaProvider"/> and
/// <see cref="AnthropicProvider"/> did before this seam was extracted:
/// <list type="bullet">
/// <item><description>Headers from the request are stamped onto the
/// outbound <see cref="HttpRequestMessage"/>, so per-request auth headers
/// (<c>x-api-key</c>, <c>anthropic-version</c>) flow through unchanged.</description></item>
/// <item><description>A non-empty body without an explicit
/// <c>Content-Type</c> header defaults to
/// <c>application/json</c>, matching the request shape the OSS providers
/// produce today.</description></item>
/// <item><description>Streaming uses
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/> so the caller
/// can iterate SSE chunks as they arrive without buffering the full
/// response.</description></item>
/// </list>
/// </para>
/// <para>
/// Transport failures (DNS, connection refused) propagate as
/// <see cref="HttpRequestException"/> so the calling provider's existing
/// error-mapping logic continues to work. The dispatcher-proxied path
/// collapses every transport failure into a 502 response instead — the
/// asymmetry is deliberate so we don't change the OSS error shape behind
/// the back of providers that already classify <see cref="HttpRequestException"/>.
/// </para>
/// </remarks>
public class HttpClientLlmDispatcher(
    HttpClient httpClient,
    ILoggerFactory loggerFactory) : ILlmDispatcher
{
    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ILogger _logger = loggerFactory.CreateLogger<HttpClientLlmDispatcher>();

    /// <summary>Name of the named <see cref="HttpClient"/> registered for direct LLM calls.</summary>
    public const string HttpClientName = "spring-llm-direct";

    /// <inheritdoc />
    public async Task<LlmDispatchResponse> SendAsync(
        LlmDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = BuildRequest(request);

        _logger.LogDebug("Sending direct LLM request to {Url}", request.Url);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new LlmDispatchResponse(StatusCode: (int)response.StatusCode, Body: bytes);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SendStreamingAsync(
        LlmDispatchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = BuildRequest(request);

        _logger.LogDebug("Sending direct streaming LLM request to {Url}", request.Url);

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // 8 KiB matches HttpClient's default copy buffer and SSE chunks for
        // chat-completion deltas are well below that, so we typically yield
        // one chunk per upstream emission. Larger payloads come back in
        // multiple chunks; the consumer reassembles SSE frames.
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            // Copy out so the next ReadAsync can reuse the buffer without the
            // consumer holding a reference into mutable state.
            var chunk = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunk, 0, read);
            yield return chunk;
        }
    }

    private static HttpRequestMessage BuildRequest(LlmDispatchRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, request.Url);

        ByteArrayContent? content = null;
        if (request.Body.Length > 0)
        {
            content = new ByteArrayContent(request.Body);
            message.Content = content;
        }

        var contentTypeOverridden = false;
        if (request.Headers is { Count: > 0 } headers)
        {
            foreach (var (name, value) in headers)
            {
                // Content-* headers belong on the content, not the request — same
                // rule the BCL enforces. Default everything else onto the request
                // headers; the BCL's TryAddWithoutValidation covers the auth
                // headers (x-api-key, anthropic-version) the providers stamp.
                if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        contentTypeOverridden = true;
                    }
                    content?.Headers.TryAddWithoutValidation(name, value);
                }
                else
                {
                    message.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        if (content is not null && !contentTypeOverridden)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        }

        return message;
    }
}