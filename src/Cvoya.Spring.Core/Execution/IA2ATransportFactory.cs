// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Selects the correct <see cref="IA2ATransport"/> implementation for a given
/// agent container based on the caller's network position and the target
/// container's reachability.
/// </summary>
/// <remarks>
/// <para>
/// The factory encapsulates the path-selection logic that previously lived
/// inline in <c>A2AExecutionDispatcher.SendA2AMessageAsync</c>. Callers
/// receive a pre-selected, pre-configured transport and do not branch on
/// network topology themselves.
/// </para>
/// <para>
/// The decision criteria are:
/// <list type="bullet">
///   <item>
///     When a <paramref name="containerId"/> is provided and the caller
///     cannot directly reach the agent's network, returns a dispatcher-proxy
///     transport that routes every A2A POST through
///     <see cref="IContainerRuntime.SendHttpJsonAsync"/>.
///   </item>
///   <item>
///     When the caller has direct L3 reachability to the agent (e.g. the
///     worker is dual-homed or running on the same network), returns a
///     direct-HTTP transport that uses a plain <see cref="System.Net.Http.HttpClient"/>.
///   </item>
/// </list>
/// </para>
/// <para>
/// In the current OSS deployment the worker always routes through the
/// dispatcher proxy (ADR 0028); a future deployment where the worker is
/// dual-homed would pre-register a factory implementation that returns the
/// direct transport for reachable containers.
/// </para>
/// </remarks>
public interface IA2ATransportFactory
{
    /// <summary>
    /// Returns the <see cref="IA2ATransport"/> to use for an A2A roundtrip
    /// to the named agent container.
    /// </summary>
    /// <param name="containerId">
    /// The identifier of the target agent container as known to the container
    /// runtime. Used by proxy-style transports to route the request to the
    /// correct container. May be <c>null</c> in test harness scenarios where
    /// the agent is reachable via a real HTTP endpoint without a container
    /// backing it — in that case the factory MUST return the direct transport.
    /// </param>
    /// <returns>
    /// A transport ready to send A2A requests to the specified container.
    /// The transport is owned by the factory (not the caller); callers
    /// MUST dispose the returned transport when the A2A roundtrip completes.
    /// </returns>
    IA2ATransport CreateTransport(string? containerId);
}