// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Pluggable seam for sending hosted-agent LLM HTTP requests. Lives between
/// the <see cref="IAiProvider"/> implementations (which know <em>what</em> to
/// send — request shape, response parsing) and the actual transport (which
/// decides <em>where</em> the request executes — direct from the worker, or
/// via <c>spring-dispatcher</c> into a tenant network the worker cannot
/// reach itself).
/// </summary>
/// <remarks>
/// <para>
/// The hosted-agent LLM analogue of the dispatcher-proxied A2A primitive
/// shipped for issue #1160 — same idea, different payload. ADR 0028
/// Decision E mandates that hosted and delegated agents have symmetric
/// access to tenant-local Ollama: delegated agents (which run in agent
/// containers attached to the per-tenant bridge) reach
/// <c>tenant-ollama:11434</c> directly; hosted agents (which run
/// in-process inside the worker on <c>spring-net</c>) cannot, because the
/// worker is a single-network platform process by design (Decision A) and
/// dual-homing the worker onto every tenant network would defeat the
/// isolation that motivates the per-tenant networks in the first place.
/// </para>
/// <para>
/// Two implementations ship in the OSS core:
/// <list type="bullet">
/// <item><description><c>HttpClientLlmDispatcher</c> — sends the request
/// directly via an injected <see cref="System.Net.Http.HttpClient"/>; the
/// default for OSS where the in-cluster <c>spring-ollama</c> container is
/// dual-attached to <c>spring-net</c> and <c>spring-tenant-default</c> and
/// the worker can still resolve it on <c>spring-net</c>.</description></item>
/// <item><description><c>DispatcherProxiedLlmDispatcher</c> — wraps the
/// request, posts it to <c>spring-dispatcher</c>'s
/// <c>POST /v1/llm/forward</c> endpoint, and reconstructs the response
/// from the dispatcher's reply. The dispatcher then forwards into the
/// appropriate tenant network. This is the path the cloud overlay
/// activates and the path OSS opts into when it eventually moves
/// <c>spring-ollama</c> off <c>spring-net</c> entirely.</description></item>
/// </list>
/// </para>
/// <para>
/// The interface is deliberately HTTP-shaped — URL, body bytes, optional
/// headers — rather than a higher-level "complete this prompt" RPC,
/// because the OSS-shipped <see cref="IAiProvider"/> implementations already
/// own the OpenAI-compatible chat-completions request shape and the
/// response/SSE parsing. Slimming the seam to "send these bytes, get those
/// bytes back" keeps both implementations small and lets the cloud overlay
/// swap transports without re-implementing the provider layer.
/// </para>
/// <para>
/// The terminal architecture (#1170) eventually moves hosted-agent
/// execution out of the worker entirely into a per-tenant agents-host
/// container; at that point the worker stops calling
/// <see cref="IAiProvider"/> directly and the
/// <see cref="ILlmDispatcher"/> seam is what the future migration swaps to
/// re-route LLM execution. Keeping it small now keeps that swap small.
/// </para>
/// </remarks>
public interface ILlmDispatcher
{
    /// <summary>
    /// Sends a single-shot LLM HTTP <c>POST</c> and returns the response.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The HTTP response. Implementations preserve the upstream status code
    /// and body verbatim; transport failures (DNS, connection refused, the
    /// dispatcher itself unavailable) surface as a 502 with an empty body so
    /// callers' retry / failover logic owns the policy uniformly. The
    /// caller — typically an <see cref="IAiProvider"/> — interprets the
    /// status and parses the body shape.
    /// </returns>
    Task<LlmDispatchResponse> SendAsync(LlmDispatchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a streaming LLM HTTP <c>POST</c> and yields response body chunks
    /// as they arrive. The body is emitted verbatim in the order the upstream
    /// produced it; SSE framing is left to the caller to parse so the seam
    /// stays transport-only. Implementations terminate the enumeration
    /// cleanly on upstream EOF or cancellation; transport failures throw so
    /// the caller's retry policy can react.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An asynchronous enumeration of response body chunks.</returns>
    IAsyncEnumerable<ReadOnlyMemory<byte>> SendStreamingAsync(LlmDispatchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// HTTP request shape for <see cref="ILlmDispatcher"/>. Carried by value
/// because both implementations are a thin pass-through over the wire.
/// </summary>
/// <param name="Url">
/// Absolute URL the worker would have called directly (e.g.
/// <c>http://tenant-ollama:11434/v1/chat/completions</c>). The dispatcher-
/// proxied implementation uses this verbatim as the in-tenant target URL;
/// the direct implementation hands it straight to <c>HttpClient</c>.
/// </param>
/// <param name="Body">
/// UTF-8 request body bytes. Empty array is allowed (some endpoints accept
/// no body); <c>null</c> is not — pass an empty array instead so the wire
/// stays unambiguous.
/// </param>
/// <param name="Headers">
/// Optional request headers (e.g. <c>x-api-key</c> for managed providers,
/// <c>anthropic-version</c>). The dispatcher-proxied implementation
/// forwards these onto the upstream call verbatim; the direct
/// implementation stamps them onto the outbound <c>HttpRequestMessage</c>.
/// <c>Content-Type: application/json</c> is set by the implementation when
/// the body is non-empty and no <c>Content-Type</c> override is supplied,
/// matching the request shape <see cref="IAiProvider"/> producers send today.
/// </param>
public record LlmDispatchRequest(
    string Url,
    byte[] Body,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// HTTP response shape returned by <see cref="ILlmDispatcher.SendAsync"/>.
/// </summary>
/// <param name="StatusCode">HTTP status code (e.g. 200, 404, 502).</param>
/// <param name="Body">Response body bytes; empty when the upstream returned no body.</param>
public record LlmDispatchResponse(
    int StatusCode,
    byte[] Body);