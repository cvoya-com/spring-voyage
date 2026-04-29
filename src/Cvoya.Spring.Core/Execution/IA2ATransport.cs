// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using System.Net.Http;

/// <summary>
/// Named transport seam for A2A 0.3.x message-send roundtrips to agent
/// containers. Every A2A call the platform makes to a running agent flows
/// through a single implementation of this interface so auth, routing, and
/// network-position decisions are encapsulated here and not threaded through
/// every dispatch callsite.
/// </summary>
/// <remarks>
/// <para>
/// Two implementations ship in the platform:
/// <list type="bullet">
///   <item>
///     <b>Direct</b> — uses a plain <see cref="HttpClient"/> when the caller
///     already has L3 reachability to the agent container's network namespace
///     (e.g. the worker is dual-homed on both <c>spring-net</c> and the
///     per-tenant bridge, or the agent runs on the same loopback). This path
///     avoids the extra hop through <c>spring-dispatcher</c>.
///   </item>
///   <item>
///     <b>Dispatcher-proxy</b> — forwards every outbound HTTP POST through
///     <see cref="IContainerRuntime.SendHttpJsonAsync"/> so the JSON-RPC
///     roundtrip executes from inside the agent container's own network
///     namespace. Used when the caller is on <c>spring-net</c> and cannot
///     reach the per-tenant bridge directly (the standard OSS topology per
///     ADR 0028).
///   </item>
/// </list>
/// </para>
/// <para>
/// The concrete choice between the two is made once, at dispatch time, by
/// <see cref="IA2ATransportFactory"/> — callers receive a pre-selected
/// implementation and do not branch on network topology themselves.
/// </para>
/// <para>
/// This seam is the D2 / Stage 2 deliverable described in ADR-0029. It
/// subsumes the earlier "extract IAgentTransport" cleanup noted in #1277.
/// </para>
/// </remarks>
public interface IA2ATransport : IDisposable
{
    /// <summary>
    /// Returns an <see cref="HttpClient"/> configured to send A2A 0.3.x
    /// JSON-RPC requests to the specified agent container endpoint. The
    /// returned client has the correct base address, any required auth
    /// headers, and the transport-specific <see cref="HttpMessageHandler"/>
    /// already wired in.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for disposing the returned
    /// <see cref="HttpClient"/>; the transport itself is long-lived and
    /// reusable across calls (the client, by contrast, is per-call).
    /// </remarks>
    /// <param name="endpoint">
    /// The A2A base address of the agent container
    /// (e.g. <c>http://localhost:8999/</c>). The transport uses this
    /// to construct the <see cref="HttpClient.BaseAddress"/> so the
    /// caller can construct an <c>A2AClient</c> around the returned client
    /// without knowing the transport details.
    /// </param>
    /// <returns>
    /// A configured <see cref="HttpClient"/> whose requests will be routed
    /// to the agent endpoint via this transport's network path.
    /// </returns>
    HttpClient CreateHttpClient(Uri endpoint);
}