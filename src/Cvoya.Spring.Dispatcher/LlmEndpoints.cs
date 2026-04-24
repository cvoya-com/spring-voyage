// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// Endpoint map for the <c>/v1/llm</c> surface — the dispatcher-proxied
/// LLM forwarding primitive that closes the hosted-agent half of ADR 0028
/// Decision E (issue #1168). Symmetric with the dispatcher-proxied A2A
/// primitive shipped in <see cref="ContainersEndpoints.SendA2AAsync"/>:
/// the worker hands the dispatcher an HTTP envelope, the dispatcher
/// dispatches the upstream call from its own process, and returns the
/// upstream response verbatim.
/// </summary>
/// <remarks>
/// <para>
/// The dispatcher is a host process (issue #1063) and ships in
/// deployments where the worker can no longer resolve the per-tenant
/// LLM endpoint directly — either because the worker is on
/// <c>spring-net</c> only (ADR 0028 Decision A) and Ollama has moved off
/// it onto a tenant network, or because the cloud overlay replaces the
/// in-cluster Ollama with a tenant-routed reverse proxy. In both cases
/// the dispatcher executes the upstream call on the worker's behalf.
/// </para>
/// <para>
/// Forwarding strategy: the dispatcher uses a named <see cref="HttpClient"/>
/// to POST the supplied <c>url</c> directly. This works when the
/// dispatcher's host network can reach the upstream endpoint, which is
/// the OSS case today (<c>spring-ollama</c> publishes its port on the
/// host) and the cloud case (the dispatcher pod has tenant-network
/// reachability via the cluster's service mesh). Deployments where the
/// dispatcher itself cannot reach the upstream — e.g. macOS / Windows
/// developer environments where Podman runs in a VM and the tenant
/// network is unreachable from the host — are out of scope for V2 and
/// will get a follow-up that relays through a tenant-attached sidecar
/// using the same <c>podman exec ... wget</c> pattern as
/// <c>SendHttpJsonAsync</c>; tracked in #1168 follow-ups.
/// </para>
/// <para>
/// Authorization: every route requires the same bearer-token auth as
/// <c>/v1/containers</c> — the worker's per-deployment token is the only
/// credential that can forward LLM traffic, ensuring an attacker who
/// reaches the dispatcher port without the token cannot use it as an
/// open egress relay.
/// </para>
/// </remarks>
public static class LlmEndpoints
{
    /// <summary>Name of the named <see cref="HttpClient"/> the dispatcher uses to forward LLM requests.</summary>
    public const string ForwardingHttpClientName = "spring-llm-upstream";

    private static class EventIds
    {
        public static readonly EventId LlmForwardRequested =
            new(6020, nameof(LlmForwardRequested));
        public static readonly EventId LlmForwardStreamRequested =
            new(6021, nameof(LlmForwardStreamRequested));
        public static readonly EventId LlmForwardRejected =
            new(6022, nameof(LlmForwardRejected));
        public static readonly EventId LlmForwardFailed =
            new(6023, nameof(LlmForwardFailed));
    }

    /// <summary>Maps the <c>/v1/llm</c> endpoints onto the supplied route builder.</summary>
    public static IEndpointRouteBuilder MapLlmEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/llm").RequireAuthorization();

        group.MapPost("/forward", ForwardAsync);
        group.MapPost("/forward/stream", ForwardStreamAsync);

        return endpoints;
    }

    /// <summary>
    /// <c>POST /v1/llm/forward</c> — forward a single-shot LLM request
    /// through the dispatcher to the upstream provider. The wire shape
    /// mirrors <c>POST /v1/containers/{id}/a2a</c>: base64 body in,
    /// status + base64 body out. Transport failures collapse to HTTP 502.
    /// </summary>
    internal static async Task<IResult> ForwardAsync(
        [FromBody] LlmForwardRequest request,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Dispatcher.Llm");

        if (!TryValidateRequest(request, logger, out var bodyBytes, out var validationError))
        {
            return validationError;
        }

        logger.LogInformation(
            EventIds.LlmForwardRequested,
            "Forwarding LLM POST to {Url} bytes={Bytes}", request.Url, bodyBytes.Length);

        var client = httpClientFactory.CreateClient(ForwardingHttpClientName);

        try
        {
            using var upstream = BuildUpstreamRequest(request, bodyBytes);
            using var response = await client.SendAsync(upstream, cancellationToken);
            var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            return Results.Ok(new LlmForwardResponse
            {
                StatusCode = (int)response.StatusCode,
                BodyBase64 = responseBytes.Length == 0 ? string.Empty : Convert.ToBase64String(responseBytes),
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Worker dropped the connection — let the framework return the
            // canceled response to the client; nothing we can do upstream.
            throw;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                EventIds.LlmForwardFailed,
                ex,
                "Upstream LLM call to {Url} failed; collapsing to 502.", request.Url);

            // 502 with empty body — same shape as the A2A failure mode.
            // The worker's DispatcherProxiedLlmDispatcher unwraps this to
            // LlmDispatchResponse(502, []) and the calling IAiProvider's
            // own retry / failover policy decides what to do next.
            return Results.Json(
                new LlmForwardResponse { StatusCode = 502, BodyBase64 = string.Empty },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// <c>POST /v1/llm/forward/stream</c> — forward a streaming LLM
    /// request and relay the upstream response body bytes back to the
    /// worker as they arrive. SSE framing is preserved verbatim; the
    /// caller (typically <see cref="OllamaProvider"/> or
    /// <see cref="AnthropicProvider"/>) parses the SSE event boundaries.
    /// </summary>
    internal static async Task<IResult> ForwardStreamAsync(
        [FromBody] LlmForwardRequest request,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Dispatcher.Llm");

        if (!TryValidateRequest(request, logger, out var bodyBytes, out var validationError))
        {
            return validationError;
        }

        logger.LogInformation(
            EventIds.LlmForwardStreamRequested,
            "Forwarding streaming LLM POST to {Url} bytes={Bytes}", request.Url, bodyBytes.Length);

        var client = httpClientFactory.CreateClient(ForwardingHttpClientName);

        HttpResponseMessage upstreamResponse;
        try
        {
            using var upstream = BuildUpstreamRequest(request, bodyBytes);
            // ResponseHeadersRead so we don't wait for the full body before
            // we start relaying — without it streaming degenerates into
            // a buffered round-trip.
            upstreamResponse = await client.SendAsync(
                upstream, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                EventIds.LlmForwardFailed,
                ex,
                "Streaming LLM call to {Url} failed; returning 502.", request.Url);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        if (!upstreamResponse.IsSuccessStatusCode)
        {
            // Don't relay an upstream non-success body — the worker's
            // streaming path treats any non-2xx as a hard failure (an
            // empty enumeration would be silently misread as "the
            // upstream gave us nothing"). Mirror the upstream status.
            var status = (int)upstreamResponse.StatusCode;
            upstreamResponse.Dispose();
            return Results.StatusCode(status);
        }

        return new StreamRelayResult(upstreamResponse);
    }

    private static bool TryValidateRequest(
        LlmForwardRequest request,
        ILogger logger,
        out byte[] bodyBytes,
        out IResult error)
    {
        bodyBytes = [];
        error = Results.Empty;

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            logger.LogWarning(
                EventIds.LlmForwardRejected,
                "Rejected LLM forward: url is required");
            error = Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "url_required",
                Message = "Field 'url' is required.",
            });
            return false;
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var parsedUrl)
            || (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogWarning(
                EventIds.LlmForwardRejected,
                "Rejected LLM forward: url is not a valid absolute http(s) URI ({Url})", request.Url);
            error = Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "url_invalid",
                Message = "Field 'url' must be an absolute http or https URI "
                    + "(e.g. http://tenant-ollama:11434/v1/chat/completions).",
            });
            return false;
        }

        if (request.BodyBase64 is null)
        {
            logger.LogWarning(
                EventIds.LlmForwardRejected,
                "Rejected LLM forward: bodyBase64 is required");
            error = Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "body_required",
                Message = "Field 'bodyBase64' is required (use an empty string for an empty body).",
            });
            return false;
        }

        try
        {
            bodyBytes = request.BodyBase64.Length == 0
                ? []
                : Convert.FromBase64String(request.BodyBase64);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(
                EventIds.LlmForwardRejected,
                ex,
                "Rejected LLM forward: bodyBase64 is not valid base64");
            error = Results.BadRequest(new DispatcherErrorResponse
            {
                Code = "body_invalid",
                Message = $"Field 'bodyBase64' is not valid base64: {ex.Message}",
            });
            return false;
        }

        return true;
    }

    private static HttpRequestMessage BuildUpstreamRequest(LlmForwardRequest request, byte[] bodyBytes)
    {
        var upstream = new HttpRequestMessage(HttpMethod.Post, request.Url);

        ByteArrayContent? content = null;
        if (bodyBytes.Length > 0)
        {
            content = new ByteArrayContent(bodyBytes);
            upstream.Content = content;
        }

        var contentTypeOverridden = false;
        if (request.Headers is { Count: > 0 } headers)
        {
            foreach (var (name, value) in headers)
            {
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
                    upstream.Headers.TryAddWithoutValidation(name, value);
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

        return upstream;
    }

    /// <summary>
    /// <see cref="IResult"/> that relays an upstream <see cref="HttpResponseMessage"/>'s
    /// body bytes to the dispatcher's HTTP response stream. The upstream
    /// response is owned by this result and disposed when execution
    /// completes — successful relay, cancellation, or upstream error.
    /// </summary>
    private sealed class StreamRelayResult(HttpResponseMessage upstream) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            try
            {
                httpContext.Response.StatusCode = (int)upstream.StatusCode;

                // Forward Content-Type so providers that key off application/x-ndjson
                // vs text/event-stream see the same value the upstream sent.
                if (upstream.Content.Headers.ContentType is { } ct)
                {
                    httpContext.Response.ContentType = ct.ToString();
                }

                await using var sourceStream = await upstream.Content.ReadAsStreamAsync(httpContext.RequestAborted);
                await sourceStream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
            }
            finally
            {
                upstream.Dispose();
            }
        }
    }
}