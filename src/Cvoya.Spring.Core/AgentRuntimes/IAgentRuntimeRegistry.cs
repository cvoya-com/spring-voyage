// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Singleton registry of all DI-registered <see cref="IAgentRuntime"/>
/// instances. The host's API layer, wizard, and CLI consume this instead of
/// referencing concrete runtime packages, which lets new runtimes drop in
/// via DI without any core code change.
/// </summary>
/// <remarks>
/// <para>
/// Lookups on <see cref="Get(string)"/> are case-insensitive against
/// <see cref="IAgentRuntime.Id"/>.
/// </para>
/// <para>
/// The default implementation lives in <c>Cvoya.Spring.Dapr</c> and
/// enumerates every <see cref="IAgentRuntime"/> registered in DI. The
/// private cloud repo may replace it with a tenant-scoped variant by
/// registering its own implementation before calling
/// <c>AddCvoyaSpringDapr</c>.
/// </para>
/// </remarks>
public interface IAgentRuntimeRegistry
{
    /// <summary>
    /// Every agent runtime registered with the host. Ordering is
    /// implementation-defined — callers that need a deterministic order
    /// should sort on <see cref="IAgentRuntime.Id"/> or
    /// <see cref="IAgentRuntime.DisplayName"/>.
    /// </summary>
    IReadOnlyList<IAgentRuntime> All { get; }

    /// <summary>
    /// Looks up a runtime by its stable <see cref="IAgentRuntime.Id"/>.
    /// Matches are case-insensitive.
    /// </summary>
    /// <param name="id">The runtime id to resolve.</param>
    /// <returns>The matching runtime, or <c>null</c> if none is registered with that id.</returns>
    IAgentRuntime? Get(string id);
}