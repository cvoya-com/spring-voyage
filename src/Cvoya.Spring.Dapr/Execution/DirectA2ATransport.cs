// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net.Http;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// <see cref="IA2ATransport"/> implementation that reaches the agent
/// container using a plain <see cref="HttpClient"/> with no additional
/// proxy hop. Suitable when the caller already has L3 reachability to
/// the agent container's network namespace — for example when the worker
/// process is dual-homed on both <c>spring-net</c> and the per-tenant
/// bridge, or in a single-network test / local-development topology.
/// </summary>
/// <remarks>
/// <para>
/// The companion transport is <see cref="DispatcherProxyA2ATransport"/>,
/// which routes every outbound POST through
/// <see cref="IContainerRuntime.SendHttpJsonAsync"/> for deployments where
/// the worker cannot reach the per-tenant bridge directly (the standard OSS
/// topology per ADR 0028).
/// </para>
/// <para>
/// Auth headers (bearer token scoped to the target agent, platform identity
/// stamps) are the caller's responsibility when constructing the
/// <see cref="HttpClient"/> returned by <see cref="CreateHttpClient"/>. The
/// direct transport does not add any auth — it is a plain, unadorned client.
/// The A2A SDK's own request body is sufficient for the container-side
/// dispatch; the platform adds auth at the API-layer Bucket-2 surface, not
/// at the worker → agent hop.
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
internal sealed class DirectA2ATransport : IA2ATransport
{
    private bool _disposed;

    /// <inheritdoc />
    public HttpClient CreateHttpClient(Uri endpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return new HttpClient
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
