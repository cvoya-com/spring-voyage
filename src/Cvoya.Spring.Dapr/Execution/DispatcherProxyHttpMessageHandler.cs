// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// <see cref="HttpMessageHandler"/> that forwards every outbound HTTP
/// request through <see cref="IContainerRuntime.SendHttpJsonAsync"/> so the
/// request executes inside the named agent container's network namespace
/// rather than over the worker's own loopback. This is the seam that closes
/// the message-send half of issue #1160 — the worker is on
/// <c>spring-net</c>, the agent is on <c>spring-tenant-&lt;id&gt;</c>, and
/// the worker has no L3 route into the tenant network. Routing through the
/// dispatcher-proxied primitive sidesteps the network split without
/// attaching the dispatcher itself to tenant networks (see ADR 0028).
/// </summary>
/// <remarks>
/// <para>
/// Wired underneath the third-party <c>A2A.A2AClient</c> in
/// <see cref="A2AExecutionDispatcher.SendA2AMessageAsync"/>. The A2A SDK
/// sees a normal <see cref="HttpClient"/> and goes through its usual
/// JSON-RPC request shape; this handler intercepts the
/// <see cref="HttpRequestMessage"/> and translates it into a single
/// <c>POST /v1/containers/{id}/a2a</c> on the dispatcher.
/// </para>
/// <para>
/// The contract is intentionally narrow: only POST + JSON body roundtrips
/// are supported, mirroring the narrow surface
/// <see cref="IContainerRuntime.SendHttpJsonAsync"/> exposes. Any other
/// HTTP verb (the readiness probe is a GET, but it goes through
/// <see cref="IContainerRuntime.ProbeContainerHttpAsync"/>, not this
/// handler) throws so a future code path that tries to widen the contract
/// trips loudly rather than silently misroutes.
/// </para>
/// </remarks>
internal sealed class DispatcherProxyHttpMessageHandler(
    IContainerRuntime containerRuntime,
    string containerId) : HttpMessageHandler
{
    private readonly IContainerRuntime _containerRuntime = containerRuntime
        ?? throw new ArgumentNullException(nameof(containerRuntime));
    private readonly string _containerId = string.IsNullOrWhiteSpace(containerId)
        ? throw new ArgumentException("Container id is required.", nameof(containerId))
        : containerId;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post)
        {
            throw new NotSupportedException(
                $"DispatcherProxyHttpMessageHandler only supports POST; got {request.Method}. " +
                "Readiness probes go through IContainerRuntime.ProbeHttpFromHostAsync; " +
                "any other HTTP traffic to the agent must add a new IContainerRuntime primitive.");
        }

        if (request.RequestUri is null)
        {
            throw new InvalidOperationException(
                "A2A request had no RequestUri; the A2AClient must be constructed with a base address.");
        }

        var bodyBytes = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        var inContainerUrl = request.RequestUri.IsAbsoluteUri
            ? request.RequestUri.ToString()
            : new Uri(new Uri("http://localhost/"), request.RequestUri).ToString();

        var proxied = await _containerRuntime.SendHttpJsonAsync(
            _containerId, inContainerUrl, bodyBytes, cancellationToken);

        var response = new HttpResponseMessage((HttpStatusCode)proxied.StatusCode)
        {
            Content = new ByteArrayContent(proxied.Body),
            RequestMessage = request,
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        return response;
    }
}