// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// Default <see cref="IA2ATransportFactory"/> for the OSS deployment where
/// the worker runs on <c>spring-net</c> and cannot reach per-tenant agent
/// containers directly (ADR 0028).
/// </summary>
/// <remarks>
/// <para>
/// Decision logic:
/// <list type="bullet">
///   <item>
///     When <c>containerId</c> is provided: returns a
///     <see cref="DispatcherProxyA2ATransport"/> that routes every A2A
///     POST through <see cref="IContainerRuntime.SendHttpJsonAsync"/>.
///   </item>
///   <item>
///     When <c>containerId</c> is <c>null</c>: returns a
///     <see cref="DirectA2ATransport"/> that uses a plain
///     <see cref="System.Net.Http.HttpClient"/>. This covers test-harness
///     scenarios and future dual-homed deployments where the caller has
///     direct L3 reachability to the agent.
///   </item>
/// </list>
/// </para>
/// <para>
/// A private-cloud or dual-homed deployment that wants the direct transport
/// for all containers can pre-register an alternative
/// <see cref="IA2ATransportFactory"/> before calling
/// <c>AddCvoyaSpringDapr</c>. The factory registered first wins
/// (<c>TryAdd</c> semantics).
/// </para>
/// </remarks>
public sealed class DispatcherProxyA2ATransportFactory(IContainerRuntime containerRuntime) : IA2ATransportFactory
{
    private readonly IContainerRuntime _containerRuntime = containerRuntime
        ?? throw new ArgumentNullException(nameof(containerRuntime));

    /// <inheritdoc />
    public IA2ATransport CreateTransport(string? containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            // No container id — the agent is reachable via a real HTTP
            // endpoint in the caller's network (test harness, future
            // dual-homed topology). Use direct transport.
            return new DirectA2ATransport();
        }

        return new DispatcherProxyA2ATransport(_containerRuntime, containerId);
    }
}