// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net.Http;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// <see cref="IA2ATransport"/> implementation that forwards every outbound
/// A2A HTTP POST through <see cref="IContainerRuntime.SendHttpJsonAsync"/>
/// so the JSON-RPC roundtrip executes from inside the agent container's own
/// network namespace. Used when the caller is on <c>spring-net</c> and
/// cannot reach the per-tenant bridge directly (the standard OSS topology
/// per ADR 0028 / issue #1160).
/// </summary>
/// <remarks>
/// <para>
/// The underlying proxy mechanism is <see cref="DispatcherProxyHttpMessageHandler"/>,
/// which translates every outbound <see cref="HttpRequestMessage"/> into a
/// single <c>POST /v1/containers/{id}/a2a</c> call on the dispatcher. The
/// dispatcher executes the request from inside the named container's network
/// namespace via <c>podman exec -i ... curl</c> (or equivalent), so
/// <c>localhost:{port}</c> resolves to the agent's own loopback rather than
/// the worker's.
/// </para>
/// <para>
/// The companion transport is <see cref="DirectA2ATransport"/>, which is
/// returned by <see cref="DispatcherProxyA2ATransportFactory"/> when a
/// container id is not available (test harness or future dual-homed
/// topology).
/// </para>
/// <para>
/// Timeout: <see cref="HttpClient.Timeout"/> is set to
/// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>. A persistent-
/// agent turn can legitimately take minutes (Claude Code agentic loop, slow
/// model, large tool fan-out). The .NET default of 100 s would fire mid-turn
/// and throw <see cref="TaskCanceledException"/>; the dispatch's
/// <c>finally</c> would then revoke the per-turn MCP session while the
/// agent's CLI is still running, producing the 401-loop in #2718. The real
/// dispatch deadline lives elsewhere (actor turn cancellation, container
/// teardown) — same rationale as
/// <see cref="DispatcherClientOptions.RequestTimeout"/>.
/// </para>
/// </remarks>
internal sealed class DispatcherProxyA2ATransport(
    IContainerRuntime containerRuntime,
    string containerId) : IA2ATransport
{
    private readonly IContainerRuntime _containerRuntime = containerRuntime
        ?? throw new ArgumentNullException(nameof(containerRuntime));
    private readonly string _containerId = string.IsNullOrWhiteSpace(containerId)
        ? throw new ArgumentException("Container id is required.", nameof(containerId))
        : containerId;

    private bool _disposed;

    /// <inheritdoc />
    public HttpClient CreateHttpClient(Uri endpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var handler = new DispatcherProxyHttpMessageHandler(_containerRuntime, _containerId);
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = endpoint,
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
    }
}
